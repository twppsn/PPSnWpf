﻿#region -- copyright --
//
// Licensed under the EUPL, Version 1.1 or - as soon they will be approved by the
// European Commission - subsequent versions of the EUPL(the "Licence"); You may
// not use this work except in compliance with the Licence.
//
// You may obtain a copy of the Licence at:
// http://ec.europa.eu/idabc/eupl
//
// Unless required by applicable law or agreed to in writing, software distributed
// under the Licence is distributed on an "AS IS" basis, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, either express or implied. See the Licence for the
// specific language governing permissions and limitations under the Licence.
//
#endregion
#if DEBUG
#define _DEBUG_LUATASK
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.DE.Data;
using TecWare.DE.Networking;
using TecWare.DE.Stuff;
using TecWare.PPSn.Stuff;
using TecWare.PPSn.UI;
using static TecWare.PPSn.StuffUI;

namespace TecWare.PPSn
{
	#region -- interface IPpsLuaTaskParent ----------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public interface IPpsLuaTaskParent
	{
		/// <summary>Returns the dispatcher fo the UI thread</summary>
		Dispatcher Dispatcher { get; }
		/// <summary>Returns the trace sink.</summary>
		PpsTraceLog Traces { get; }
	} // interface IPpsLuaTaskParent

	#endregion

	#region -- interface IPpsLuaRequest -------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public interface IPpsLuaRequest
	{
		/// <summary>Implementes a redirect for the request call.</summary>
		BaseWebRequest Request { get; }
	} // interface IPpsLuaRequest

	#endregion

	#region -- class PpsLuaTask ---------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Task wrapper for the lua script code</summary>
	public sealed class PpsLuaTask : IDisposable
	{
		#region -- class ContinueTask ---------------------------------------------------

		private class ContinueTask
		{
			public int Id { get; set; }
			public object Task { get; set; }
		} // class ContinueTask

		#endregion

		#region -- class PpsLuaTaskSynchronizationContext --------------------------------

		private sealed class PpsLuaTaskSynchronizationContext : PpsSynchronizationContext, IDisposable
		{
			private WeakReference<PpsLuaTask> task;
			private Thread queueThread;
			private bool doContinue = true;

			public PpsLuaTaskSynchronizationContext(PpsLuaTask luaTask)
				: base(luaTask.cancellationToken)
			{
				this.task = new WeakReference<PpsLuaTask>(luaTask);

				ThreadPool.QueueUserWorkItem(ExecuteMessageLoop);
			} // ctor

			public void Dispose()
			{
				doContinue = false;
				TasksFilled.Set();
			} // proc Dispose

			private void ExecuteMessageLoop(object state)
			{
				var oldContext = Current;
				SetSynchronizationContext(this);

				// mark the thread
				this.queueThread = Thread.CurrentThread;

				try
				{
					while (Continue)
					{
						if (CancellationToken.IsCancellationRequested)
						{
							doContinue = false;
							break;
						}
						else // execute tasks in this thread
							ProcessMessageLoopUnsafe();

						TasksFilled.Wait();
					}
				}
				catch (Exception e)
				{
					if (task.TryGetTarget(out var t))
						t.SetException(e);
					else
						throw;
				}
				finally
				{
					SetSynchronizationContext(oldContext);
				}
			} // proc ExecuteMessageLoop

			protected override Thread QueueThread => queueThread;
			protected override bool Continue => doContinue;
		} // class PpsLuaTaskSynchronizationContext

		#endregion

		private readonly IPpsLuaTaskParent parent;
		private readonly CancellationToken cancellationToken;
		private readonly CancellationTokenSource cancellationTokenSource;
		private readonly SynchronizationContext context;

		private int? threadId = null;
		private int executedTaskId = 0;
		private int currentTaskId = 0;
		private int? lastTaskId = null;

		private readonly object executionLock = new object();
		private LuaResult currentResult;
		private Exception currentException = null;
		private bool isDisposed = false;
		
		internal PpsLuaTask(IPpsLuaTaskParent parent, SynchronizationContext context, CancellationToken cancellationToken, LuaResult startArguments)
		{
			this.parent = parent;
			this.cancellationToken = cancellationToken;
			// the synchronization context must not have parallelity, we enforce this with the thread id
			this.context = context ?? new PpsLuaTaskSynchronizationContext(this);
			this.currentResult = startArguments;
		} // ctor

		internal PpsLuaTask(IPpsLuaTaskParent parent, SynchronizationContext context, CancellationTokenSource cancellationTokenSource, LuaResult startArguments)
			: this(parent, context, cancellationTokenSource?.Token ?? CancellationToken.None, startArguments)
		{
			this.cancellationTokenSource = cancellationTokenSource;
		} // ctor

		~PpsLuaTask()
		{
			if (!isDisposed)
			{
				isDisposed = true;
				DisposeContext();
			}
		} // dtor

		void IDisposable.Dispose()
			=> Await();

		private void DisposeContext()
		{
			if (context is PpsLuaTaskSynchronizationContext ctx)
				ctx.Dispose();
		} // proc DisposeContext

		private void CheckDisposed()
		{
			if (isDisposed)
				throw new ObjectDisposedException(nameof(PpsLuaTask));
		} // proc CheckDisposed

		private void VerifyThreadAccess()
		{
			if (threadId.HasValue)
			{
				if (threadId.Value != Thread.CurrentThread.ManagedThreadId)
					throw new InvalidOperationException($"Task can only run in one context (threadid {threadId.Value} and continued in thread {Thread.CurrentThread.ManagedThreadId})");
			}
			else
				threadId = Thread.CurrentThread.ManagedThreadId;
		} // proc VerifyThreadAccess

		private void VerifyContinueTask(int expectedTaskId, int taskId)
		{
			lock (executionLock)
			{
				if (expectedTaskId != taskId)
					throw new InvalidOperationException($"Task is not in sequence (the context is multithreaded {context.GetType().Name}, expected id: {expectedTaskId}, actual id: {taskId}");
			}
		} // proc VerifyContinueTask

		private LuaResult Invoke(object func, params object[] args)
		{
			switch(func)
			{
				case null:
					throw new ArgumentException(nameof(func));
				default:
					return new LuaResult(Lua.RtInvoke(func, args));
			}
		} // proc Invoke

		private void ExecuteContinue(object continueWith)
		{
			// check thread
			VerifyThreadAccess();

			var continueTask = (ContinueTask)continueWith;
			try
			{
				// check for cancel or exception
				if (cancellationToken.IsCancellationRequested || IsFaulted)
					return;

				// check sequence
				VerifyContinueTask(executedTaskId + 1, continueTask.Id);

				// execute task
				try
				{
					currentResult = Invoke(continueTask.Task, currentResult.Values);
				}
				catch (TaskCanceledException)
				{
					if (!cancellationToken.IsCancellationRequested)
						cancellationTokenSource?.Cancel();
				}
				catch (Exception e)
				{
					SetException(e);
				}

				// mark task as executed
				lock (executionLock)
					executedTaskId = continueTask.Id;
			}
			finally
			{
				if (continueTask is IDisposable d)
					d.Dispose();
			}
		} // proc ExecuteContinue

		private void SetException(Exception e)
		{
			if (currentException != null)
				throw new InvalidOperationException(); // should never raised
			currentException = e;
			parent.Traces.AppendException(e);
		} // proc SetException

		private void ExecuteException(object state)
		{
			VerifyThreadAccess();
			VerifyContinueTask(lastTaskId.Value, executedTaskId + 1);

			Invoke(state, currentException.GetInnerException());
		} // proc ExecuteException

		private void ExecuteFinally(object state)
		{
			VerifyThreadAccess();
			VerifyContinueTask(lastTaskId.Value, executedTaskId + 1);

			Invoke(state, null);
		} // proc ExecuteFinally

		private void ExecuteAwait(object state)
		{
			VerifyThreadAccess();

			var taskCompletionSource = (TaskCompletionSource<LuaResult>)state;
			if (cancellationToken.IsCancellationRequested)
				taskCompletionSource.TrySetCanceled();
			else if (IsFaulted)
				taskCompletionSource.TrySetException(currentException.GetInnerException());
			else
			{
				VerifyContinueTask(lastTaskId.Value, executedTaskId + 1);
				taskCompletionSource.SetResult(currentResult);
			}

			// do not call dtor
			GC.SuppressFinalize(this);
			isDisposed = true;

			// dispose context
			DisposeContext();
		} // proc ExecuteAwait

		public PpsLuaTask Continue(object continueWith)
		{
			int GetNextId()
			{
				lock (executionLock)
				{
					if (lastTaskId.HasValue)
						throw new InvalidOperationException($"The execution thread is closed ({nameof(Await)}, {nameof(OnException)} or {nameof(Await)}).");
					return ++currentTaskId;
				}
			} // func GetNextId

			if (continueWith == null)
				throw new ArgumentNullException(nameof(continueWith));

			CheckDisposed();
			context.Post(ExecuteContinue, new ContinueTask() { Id = GetNextId(), Task = continueWith });
			return this;
		} // proc Continue

		private void CloseTaskList()
		{
			lock (executionLock)
			{
				CheckDisposed();
				lastTaskId = currentTaskId;
			}
		} // proc CloseTaskList

		public PpsLuaTask OnException(object onException)
		{
			CloseTaskList();
			context.Post(ExecuteException, onException ?? throw new ArgumentNullException(nameof(onException)));
			return this;
		} // proc OnException

		public PpsLuaTask OnFinally(object onFinish)
		{
			CloseTaskList();
			if (onFinish != null)
				context.Post(ExecuteFinally, onFinish);
			return this;
		} // proc OnFinish

		public void Cancel()
			=> cancellationTokenSource?.Cancel();

		public LuaResult Await()
		{
			CloseTaskList();

			var taskComletionSource = new TaskCompletionSource<LuaResult>();
			context.Post(ExecuteAwait, taskComletionSource);
			return taskComletionSource.Task.AwaitTask();
		} // func Await

		/// <summary>Is this thread cancelable.</summary>
		public bool CanCancel => cancellationTokenSource != null;
		/// <summary>Access to cancellation token source.</summary>
		public CancellationTokenSource CancellationTokenSource => cancellationTokenSource;

		/// <summary>Is the execution thread faulted.</summary>
		public bool IsFaulted => currentException != null;
		/// <summary>Is the execution thread canceled.</summary>
		public bool IsCanceled => cancellationToken.IsCancellationRequested;
		/// <summary>Is the execution thread completed.</summary>
		public bool IsCompleted
		{
			get
			{
				lock (executionLock)
					return lastTaskId.HasValue && lastTaskId.Value == currentTaskId;
			}
		} // prop IsCompleted
	} // class PpsLuaTask

	#endregion

	public partial class PpsEnvironment : IPpsLuaTaskParent, IPpsLuaRequest
	{
		#region -- class LuaTraceLineDebugInfo --------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class LuaTraceLineDebugInfo : ILuaDebugInfo
		{
			private readonly string chunkName;
			private readonly string sourceFile;
			private readonly int line;

			public LuaTraceLineDebugInfo(LuaTraceLineExceptionEventArgs e, string sourceFile)
			{
				this.chunkName = e.SourceName;
				this.sourceFile = sourceFile;
				this.line = e.SourceLine;
			} // ctor

			public string ChunkName => chunkName;
			public int Column => 0;
			public string FileName => sourceFile;
			public int Line => line;
		} // class LuaTraceLineDebugInfo

		#endregion

		#region -- class LuaEnvironmentTraceLineDebugger ----------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class LuaEnvironmentTraceLineDebugger : LuaTraceLineDebugger
		{
			protected override void OnExceptionUnwind(LuaTraceLineExceptionEventArgs e)
			{
				var luaFrames = new List<LuaStackFrame>();
				var offsetForRecalc = 0;
				LuaExceptionData currentData = null;

				// get default exception data
				if (e.Exception.Data[LuaRuntimeException.ExceptionDataKey] is LuaExceptionData)
				{
					currentData = LuaExceptionData.GetData(e.Exception);
					offsetForRecalc = currentData.Count;
					luaFrames.AddRange(currentData);
				}
				else
					currentData = LuaExceptionData.GetData(e.Exception, resolveStackTrace: false);

				// re-trace the stack frame
				var trace = new StackTrace(e.Exception, true);
				for (var i = offsetForRecalc; i < trace.FrameCount - 1; i++)
					luaFrames.Add(LuaExceptionData.GetStackFrame(trace.GetFrame(i)));

				// add trace point
				luaFrames.Add(new LuaStackFrame(trace.GetFrame(trace.FrameCount - 1), new LuaTraceLineDebugInfo(e, e.SourceName)));

				currentData.UpdateStackTrace(luaFrames.ToArray());
			} // proc OnExceptionUnwind
		} // class LuaEnvironmentTraceLineDebugger

		#endregion

		private LuaCompileOptions luaOptions;

		private void CreateLuaCompileOptions()
		{
			luaOptions = new LuaCompileOptions()
			{
				DebugEngine = new LuaEnvironmentTraceLineDebugger()
			};
		} // CreateLuaCompileOptions

		#region -- Lua Compiler -----------------------------------------------------------

		/// <summary>Compiles a chunk in the background.</summary>
		/// <param name="tr">Chunk source.</param>
		/// <param name="sourceLocation">Source location for the debug information.</param>
		/// <param name="throwException">If the compile fails, should be raised a exception.</param>
		/// <param name="arguments">Argument definition for the chunk.</param>
		/// <returns>Compiled chunk</returns>
		public async Task<LuaChunk> CompileAsync(TextReader tr, string sourceLocation, bool throwException, params KeyValuePair<string, Type>[] arguments)
		{
			try
			{
				return await Task.Run(() => Lua.CompileChunk(tr, sourceLocation, luaOptions, arguments));
			}
			catch (LuaParseException e)
			{
				if (throwException)
					throw;
				else
				{
					await ShowExceptionAsync(ExceptionShowFlags.Background, e, $"Compile for {sourceLocation} failed.");
					return null;
				}
			}
		} // func CompileAsync

		/// <summary>Compiles a chunk in the background.</summary>
		/// <param name="sourceCode">Source code of the chunk.</param>
		/// <param name="sourceLocation">Source location for the debug information.</param>
		/// <param name="throwException">If the compile fails, should be raised a exception.</param>
		/// <param name="arguments">Argument definition for the chunk.</param>
		/// <returns>Compiled chunk</returns>
		public async Task<LuaChunk> CompileAsync(string sourceCode, string sourceFileName, bool throwException, params KeyValuePair<string, Type>[] arguments)
		{
			using (var tr = new StringReader(sourceCode))
				return await CompileAsync(tr, sourceFileName, throwException, arguments);
		} // func CompileAsync

		/// <summary>Compiles a chunk in the background.</summary>
		/// <param name="xSource">Source element of the chunk. The Value is the source code, and the positions encoded in the tag (see GetXmlPositionFromAttributes).</param>
		/// <param name="throwException">If the compile fails, should be raised a exception.</param>
		/// <param name="arguments">Argument definition for the chunk.</param>
		/// <returns>Compiled chunk</returns>
		public Task<LuaChunk> CompileAsync(XElement xSource, bool throwException, params KeyValuePair<string, Type>[] arguments)
		{
			var code = xSource.Value;
			var pos = PpsXmlPosition.GetXmlPositionFromAttributes(xSource);
			return CompileAsync(code, pos.LineInfo ?? "dummy.lua", throwException, arguments);
		} // func CompileAsync

		/// <summary>Load an compile the file from a remote source.</summary>
		/// <param name="source">Source uri</param>
		/// <param name="throwException">Throw an exception on fail</param>
		/// <param name="arguments">Argument definition for the chunk.</param>
		/// <returns>Compiled chunk</returns>
		public async Task<LuaChunk> CompileAsync(BaseWebRequest request, Uri source, bool throwException, params KeyValuePair<string, Type>[] arguments)
		{
			try
			{
				using (var r = await request.GetResponseAsync(source.ToString()))
				{
					var contentDisposition = r.GetContentDisposition(true);
					using (var sr = request.GetTextReader(r, MimeTypes.Text.Plain))
						return await CompileAsync(sr, contentDisposition.FileName, throwException, arguments);
				}
			}
			catch (Exception e)
			{
				if (throwException)
					throw;
				else if (e is LuaParseException) // alread handled
					return null;
				else
				{
					await ShowExceptionAsync(ExceptionShowFlags.Background, e, $"Compile failed for {source}.");
					return null;
				}
			}
		} // func CompileAsync

		/// <summary>Compile the chunk in a background thread and hold the UI thread</summary>
		/// <param name="xCode"></param>
		/// <param name="throwException"></param>
		/// <param name="arguments"></param>
		/// <returns></returns>
		public LuaChunk CreateChunk(XElement xCode, bool throwException, params KeyValuePair<string, Type>[] arguments)
		{
			if (xCode == null)
				return null;

			return RunAsync(() => CompileAsync(xCode, throwException, arguments)).AwaitTask();
		} // func CreateChunk

		/// <summary>Executes the script (the script is always execute in the UI thread).</summary>
		/// <param name="chunk"></param>
		/// <param name="env"></param>
		/// <param name="throwException"></param>
		/// <param name="arguments"></param>
		/// <returns></returns>
		public LuaResult RunScript(LuaChunk chunk, LuaTable env, bool throwException, params object[] arguments)
		{
			try
			{
				return chunk.Run(env, arguments);
			}
			catch (LuaException ex)
			{
				if (throwException)
					throw;
				else
				{
					// notify exception as warning
					Traces.AppendException(ex, "Execution failed.", PpsTraceItemType.Warning);
					return LuaResult.Empty;
				}
			}
		} // func RunScript

		/// <summary>Executes the script, and returns a value (the script is always execute in the UI thread).</summary>
		/// <typeparam name="T">Type of the value.</typeparam>
		/// <param name="chunk"></param>
		/// <param name="env"></param>
		/// <param name="returnOnException"></param>
		/// <param name="arguments"></param>
		/// <returns></returns>
		public T RunScriptWithReturn<T>(LuaChunk chunk, LuaTable env, T? returnOnException, params object[] arguments)
			where T : struct
		{
			var r = chunk != null ? RunScript(chunk, env, false, arguments) : LuaResult.Empty;
			if (r.Count == 0)
			{
				if (returnOnException.HasValue)
					return returnOnException.Value;
				else
					throw new ArgumentNullException("chunk");
			}
			else
				return r[0].ChangeType<T>();
		} // func RunScriptWithReturn

		#endregion

		#region -- Async/Await Lua ------------------------------------------------------

		private Task RunBackgroundInternal(Func<Task> task, string name, CancellationToken cancellationToken)
			=> new PpsSingleThreadSynchronizationContext(name, cancellationToken, task).Task;

		/// <summary>Creates a new execution thread for the function in the background.</summary>
		/// <param name="action">Action to run.</param>
		/// <param name="name">name of the background thread</param>
		/// <param name="cancellationToken">cancellation option</param>
		public Task RunAsync(Func<Task> task, string name, CancellationToken cancellationToken)
			=> RunBackgroundInternal(task, name, cancellationToken);

		public Task RunAsync(Func<Task> task)
			=> RunAsync(task, "Worker", CancellationToken.None);

		/// <summary>Creates a new execution thread for the function in the background.</summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="func"></param>
		/// <returns></returns>
		public async Task<T> RunAsync<T>(Func<Task<T>> task, string name, CancellationToken cancellationToken)
		{
			var returnValue = default(T);
			await RunBackgroundInternal(async () => returnValue = await task(), name, cancellationToken);
			return returnValue;
		} // proc RunTaskBackground

		public Task<T> RunAsync<T>(Func<Task<T>> task)
			=> RunAsync(task, "Worker", CancellationToken.None);

		#endregion

		#region -- LuaHelper --------------------------------------------------------------

		/// <summary>Show a simple message box.</summary>
		/// <param name="text"></param>
		/// <param name="caption"></param>
		/// <param name="button"></param>
		/// <param name="image"></param>
		/// <param name="defaultResult"></param>
		/// <returns></returns>
		[LuaMember("msgbox")]
		private MessageBoxResult LuaMsgBox(string text, string caption, MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.Information, MessageBoxResult defaultResult = MessageBoxResult.OK)
			=> MsgBox(text, button, image, defaultResult);

		[LuaMember("trace")]
		private void LuaTrace(PpsTraceItemType type, params object[] args)
		{
			if (args == null || args.Length == 0)
				return;

			if (args[0] is string)
				Traces.AppendText(type, String.Format((string)args[0], args.Skip(1).ToArray()));
			else
				Traces.AppendText(type, String.Join(", ", args));
		} // proc LuaTrace

		/// <summary>Send a simple notification to the internal log</summary>
		/// <param name="args"></param>
		[LuaMember("print")]
		private void LuaPrint(params object[] args)
		{
			LuaTrace(PpsTraceItemType.Information, args);
		} // proc LuaPrint

		[LuaMember("toTable")]
		private LuaTable LuaToTable(object table)
		{
			if (table == null)
				return null;
			else if (table is LuaTable)
				return (LuaTable)table;
			else if (table is IDataRow)
			{
				var r = new LuaTable();
				var row = (IDataRow)table;
				var i = 0;
				foreach (var c in row.Columns)
					r[c.Name] = row[i++];
				return r;
			}
			else if (table is IEnumerable<PropertyValue>)
			{
				var r = new LuaTable();
				foreach (var p in (IEnumerable<PropertyValue>)table)
					r[p.Name] = p.Value;
				return r;
			}
			else
				throw new ArgumentException();
		} // func LuaToTable

		[LuaMember("typeof")]
		private Type LuaType(object o)
			=> o?.GetType();

		[LuaMember("require", true)]
		private LuaResult LuaRequire(LuaTable self, string path)
		{
			// get the current root
			var webRequest = self.GetMemberValue(nameof(IPpsLuaRequest.Request)) as BaseWebRequest ?? Request;

			// compile code, synchonize the code to this thread
			var chunk = CompileAsync(webRequest, new Uri(path, UriKind.Relative), true, new KeyValuePair<string, Type>("self", typeof(LuaTable))).AwaitTask();
			return RunScript(chunk, self, true, self);
		} // proc LuaRequire
		
		public async Task<(XDocument xaml, LuaChunk code)> LoadXamlAsync(BaseWebRequest request, LuaTable arguments, Uri xamlUri)
		{
			try
			{
				XDocument xXaml;
				using (var r = await request.GetResponseAsync(xamlUri.ToString()))
				{
					// read the file name
					arguments["_filename"] = r.GetContentDisposition().FileName;

					// parse the xaml as xml document
					using (var sr = request.GetTextReader(r, MimeTypes.Application.Xaml))
					{
						using (var xml = XmlReader.Create(sr, Procs.XmlReaderSettings, xamlUri.ToString()))
							xXaml = await Task.Run(() => XDocument.Load(xml, LoadOptions.SetBaseUri | LoadOptions.SetLineInfo));
					}
				}

				// Load the content of the code-tag, to initialize extended functionality
				var xCode = xXaml.Root.Element(xnCode);
				var chunk = (LuaChunk)null;
				if (xCode != null)
				{
					chunk = await CompileAsync(xCode, true, new KeyValuePair<string, Type>("self", typeof(LuaTable)));
					xCode.Remove();
				}

				return (xXaml, chunk);
			}
			catch (Exception e)
			{
				throw new ArgumentException("Can not load xaml definition.\n" + xamlUri.ToString(), e);
			}
		} // func LoadXamlAsync
		
		/// <summary>Creates a wrapper for the task.</summary>
		/// <param name="func">Function will be executed in the current context as an task. A task will be wrapped and executed.</param>
		/// <param name="args"></param>
		/// <returns></returns>
		[LuaMember("await")]
		private LuaResult LuaAwait(object func)
		{
			int GetTaskType()
			{
				var t = func.GetType();
				if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>) && t.GetGenericArguments()[0].IsPublic)
					return 0;
				else if (typeof(Task).IsAssignableFrom(t))
					return 1;
				else
					return -1;
			};

			switch (func)
			{
				case null:
					throw new ArgumentNullException(nameof(func));
				case PpsLuaTask lt:
					return lt.Await();
				case Task t:
					t.AwaitTask();
					switch (GetTaskType())
					{
						case 0:
							var genericArguments = t.GetType().GetGenericArguments();
							if (genericArguments[0] == typeof(LuaResult))
								return ((Task<LuaResult>)t).Result;
							else
							{
								dynamic d = t;
								return new LuaResult(d.Result);
							}
						case 1:
							return LuaResult.Empty;
						default:
							throw new NotSupportedException($"Could not await for task ({func.GetType().Name}).");
					}
				case DispatcherOperation o:
					LuaAwait(o.Task);
					return LuaResult.Empty;
				default:
					throw new ArgumentException($"The type '{func.GetType().Name}' is not awaitable.");
			}
		} // func LuaRunTask

		private PpsLuaTask CreateLuaTask(SynchronizationContext context, object func, object[] args)
		{
			var cancellationSource = (CancellationTokenSource)null;
			var cancellationToken = CancellationToken.None;

			// find a source or a token in the arguments
			for (var i = 0; i < args.Length; i++)
			{
				if (args[i] is CancellationToken ct)
				{
					cancellationToken = ct;
					break;
				}
				else if (args[i] is CancellationTokenSource cts)
				{
					cancellationSource = cts;
					break;
				}
			}

			// create execution thread
			var t = cancellationSource != null
				? new PpsLuaTask(this, context, cancellationSource, new LuaResult(args))
				: new PpsLuaTask(this, context, cancellationToken, new LuaResult(args));

			// start with the first function
			return t.Continue(func);
		} // func CreateLuaTask

		/// <summary>Executes the function or task in an background thread.</summary>
		/// <param name="func"></param>
		/// <param name="args"></param>
		/// <returns></returns>
		[LuaMember("run")]
		public PpsLuaTask LuaRunBackground(object func, params object[] args)
			=> CreateLuaTask(null, func, args);

		/// <summary>Executes the function or task, async in the ui thread.</summary>
		/// <param name="func"></param>
		/// <param name="args"></param>
		/// <returns></returns>
		[LuaMember("async")]
		public PpsLuaTask LuaAsync(object func, params object[] args)
		{
			SynchronizationContext GetUIContext()
				=> Dispatcher.Thread == Thread.CurrentThread && SynchronizationContext.Current is DispatcherSynchronizationContext
					? SynchronizationContext.Current
					: new DispatcherSynchronizationContext(Dispatcher);

			return CreateLuaTask(GetUIContext(), func, args);
		} // func LuaRunBackground

		[LuaMember("startSync")]
		public Task StartSync()
			=> masterData.StartSynchronization();

		public IDisposable BlockAllUI(DispatcherFrame frame, string message = null)
		{
			Thread.Sleep(200); // wait for finish
			if(frame.Continue)
				return null; // block ui
			else
				return null;
		} // proc BlockAllUI

		#endregion

		private readonly static MethodInfo convertToLuaResultTask;
	} // class PpsEnvironment
}
	