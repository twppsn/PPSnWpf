﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TecWare.PPSn
{
	#region -- enum PpsTraceItemType ----------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public enum PpsTraceItemType
	{
		Debug = 0,
		Information,
		Warning,
		Fail,
		Exception
	} // enum PpsTraceItemType

	#endregion

	#region -- class PpsTraceItemBase ---------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public abstract class PpsTraceItemBase
	{
		private DateTime stamp;

		public PpsTraceItemBase()
		{
			this.stamp = DateTime.Now;
		} // ctor

		public override int GetHashCode()
		{
			return Type.GetHashCode() ^ Message.GetHashCode();
		} // func GetHashCode

		public override bool Equals(object obj)
		{
			var item = obj as PpsTraceItemBase;
			if (item == null)
				return false;

			return item.Type == this.Type && item.Message == this.Message;
		} // func Equals

		public override string ToString()
		{
			return String.Format("{0}: {1}", Type, Message);
		} // func ToString

		public DateTime Stamp { get { return stamp; } }
		public abstract string Message { get; }

		public abstract PpsTraceItemType Type { get; }
	} // class PpsTraceItem

	#endregion

	#region -- class PpsExceptionItem ---------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class PpsExceptionItem : PpsTraceItemBase
	{
		private string message;
		private Exception exception;

		public PpsExceptionItem(string alternativeMessage, Exception exception)
		{
			if (!String.IsNullOrEmpty(alternativeMessage))
				message = alternativeMessage;
			else
				message = exception.Message;

			this.exception = exception;
		} // ctor

		public override string Message { get { return message; } }
		public Exception Exception { get { return exception; } }
		public override PpsTraceItemType Type { get { return PpsTraceItemType.Exception; } }
	} // class PpsExceptionItem

	#endregion

	#region -- class PpsTraceItem -------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class PpsTraceItem : PpsTraceItemBase
	{
		private TraceEventType eventType;
		private TraceEventCache eventCache;

		private int id;
		private string source;
		private string message;

		internal PpsTraceItem(TraceEventType eventType, TraceEventCache eventCache, string source, int id, string message)
		{
			this.eventType = eventType;
			this.eventCache = eventCache;
			this.id = id;
			this.source = source;
			this.message = message;
		} // ctor

		public override int GetHashCode()
		{
			return eventType.GetHashCode() ^ message.GetHashCode() ^ id.GetHashCode() ^ source.GetHashCode();
		} // func GetHashCode

		public override bool Equals(object obj)
		{
			var item = obj as PpsTraceItem;
			if (item == null)
				return false;
			return item.eventType == this.eventType && item.message == this.message && item.id == this.id && item.source == this.source;
		} // func Equals

		public int Id { get { return id; } }
		public string Source { get { return source; } }
		public TraceEventType EventType { get { return eventType; } }
		public TraceEventCache EventCache { get { return eventCache; } }

		public override string Message { get { return message; } }

		public override PpsTraceItemType Type
		{
			get
			{
				switch (eventType)
				{
					case TraceEventType.Critical:
					case TraceEventType.Error:
						return PpsTraceItemType.Fail;
					case TraceEventType.Warning:
						return PpsTraceItemType.Warning;
					case TraceEventType.Information:
					case TraceEventType.Resume:
					case TraceEventType.Start:
					case TraceEventType.Stop:
					case TraceEventType.Suspend:
						return PpsTraceItemType.Information;
					case TraceEventType.Transfer:
					case TraceEventType.Verbose:
						return PpsTraceItemType.Debug;
					default:
						return PpsTraceItemType.Debug;
				}
			} // prop type
		} // prop Type
	} // class PpsTraceItem

	#endregion

	#region -- class PpsTextItem --------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class PpsTextItem : PpsTraceItemBase
	{
		private PpsTraceItemType type;
		private string message;

		internal PpsTextItem(PpsTraceItemType type, string message)
		{
			this.type = type;
			this.message = message;
		} // ctor

		public override string Message { get { return message; } }
		public override PpsTraceItemType Type { get { return type; } }
	} // class PpsTextItem

	#endregion

	#region -- class PpsTraceLog --------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Collection for all collected events in the application. It connects 
	/// to the trace listener and catches exceptions.</summary>
	public sealed class PpsTraceLog : IList, INotifyCollectionChanged, INotifyPropertyChanged, IDisposable
	{
		private const int MaxTraceItems = 1 << 19;

		#region -- class PpsTraceListener -------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private class PpsTraceListener : TraceListener
		{
			private PpsTraceLog owner;
			private Dictionary<int, StringBuilder> currentLines = new Dictionary<int, StringBuilder>();

			public PpsTraceListener(PpsTraceLog owner)
			{
				this.owner = owner;
			} // ctor

			public override void Fail(string message, string detailMessage)
			{
				if (message == null)
				{
					message = detailMessage;
					detailMessage = null;
				}
				if (detailMessage != null)
					message = message + Environment.NewLine + detailMessage;

				owner.AppendItem(new PpsTextItem(PpsTraceItemType.Fail, message));
			} // proc Fail

			private static string FormatData(object data)
			{
				return Convert.ToString(data, CultureInfo.InvariantCulture);
			} // func FormatData

			public override void TraceData(TraceEventCache eventCache, string source, TraceEventType eventType, int id, object data)
			{
				TraceEvent(eventCache, source, eventType, id, data == null ? String.Empty : FormatData(data));
			} // func TraceData

			public override void TraceData(TraceEventCache eventCache, string source, TraceEventType eventType, int id, params object[] data)
			{
				var message = new StringBuilder();
				if (data != null)
					for (int i = 0; i < data.Length; i++)
						message.Append('[').Append(i).Append("] ").AppendLine(FormatData(data[i]));

				TraceEvent(eventCache, source, eventType, id, message.ToString());
			} // proc TraceData

			public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string message)
			{
				owner.AppendItem(new PpsTraceItem(eventType, eventCache, source, id, message));
			} // proc TraceEvent

			public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string format, params object[] args)
			{
				if (args != null && args.Length > 0)
					format = String.Format(format, args);
				this.TraceEvent(eventCache, source, eventType, id, format);
			} // proc TraceEvent

			public override void Write(string message)
			{
				if (String.IsNullOrEmpty(message))
					return;

				var threadId = Thread.CurrentThread.ManagedThreadId;
				StringBuilder currentLine;
				lock (currentLines)
				{
					if (!currentLines.TryGetValue(threadId, out currentLine))
						currentLines[threadId] = currentLine = new StringBuilder();
				}


				currentLine.Append(message);
				if (message.Length > 1 && message[message.Length - 1] == '\r' || message[message.Length - 1] == '\n')
				{
					WriteLine(currentLine.ToString().TrimEnd('\n', '\r'));
					lock (currentLines)
						currentLines.Remove(threadId);
				}
			} // proc Write

			public override void WriteLine(string message)
			{
				owner.AppendItem(new PpsTextItem(PpsTraceItemType.Debug, message));
			} // proc WriteLine
		} // class PpsTraceListener

		#endregion

		public event NotifyCollectionChangedEventHandler CollectionChanged;
		public event PropertyChangedEventHandler PropertyChanged;

		private PpsTraceListener listener;
		private List<PpsTraceItemBase> items = new List<PpsTraceItemBase>();
		private PpsTraceItemBase lastTrace = null;

		#region -- Ctor/Dtor --------------------------------------------------------------

		public PpsTraceLog()
		{
			Trace.Listeners.Add(listener = new PpsTraceListener(this));
		} // ctor

		~PpsTraceLog()
		{
			Dispose(false);
		} // dtor

		public void Dispose()
		{
			GC.SuppressFinalize(this);
			Dispose(true);
		} // proc Dispose

		private void Dispose(bool disposing)
		{
			Trace.Listeners.Remove(listener);
			if (disposing)
			{
				Clear();
			}
		} // proc Dispose

		#endregion

		#region -- AppendItem -------------------------------------------------------------

		private int AppendItem(PpsTraceItemBase item)
		{
			var index = -1;
			var resetList = false;
			var lastTraceChanged = false;
			object itemRemoved = null;

			// change list
			lock (items)
			{
				while (items.Count > MaxTraceItems)
				{
					if (itemRemoved == null)
						itemRemoved = items[0];
					else
						resetList = true;
					items.RemoveAt(0);
				}

				items.Add(item);
				index = items.Count - 1;

				if (item.Type == PpsTraceItemType.Exception || item.Type == PpsTraceItemType.Fail || item.Type == PpsTraceItemType.Warning)
					lastTraceChanged = SetLastTrace(item);
				else
					lastTraceChanged = UpdateLastTrace();
			}

			// update view
			if (resetList)
				OnCollectionChanged();
			else
			{
				if (itemRemoved != null)
					OnCollectionRemoved(itemRemoved);
				OnCollectionAdded(item);
			}
			OnPropertyChanged("Count");
			if (lastTraceChanged)
				OnPropertyChanged("LastTrace");

			return index;
		} // proc AppendItem

		public void Clear()
		{
			lock (items)
			{
				lastTrace = null;
				items.Clear();
			}

			OnCollectionChanged();
			OnPropertyChanged("Count");
			OnPropertyChanged("LastTrace");
		} // proc Clear

		public void AppendText(PpsTraceItemType type, string message)
		{
			AppendItem(new PpsTextItem(type, message));
		} // proc AppendText

		public void AppendException(Exception exception, string alternativeMessage = null)
		{
			AppendItem(new PpsExceptionItem(alternativeMessage, exception));
		} // proc AppendException

		#endregion

		#region -- Last Trace Item --------------------------------------------------------

		public void ClearLastTrace()
		{
			LastTrace = null;
		} // proc ClearLastTrace

		private bool IsLastTraceNear()
		{
			return lastTrace != null && (DateTime.Now - lastTrace.Stamp).TotalMilliseconds < 5000;
		} // func IsLastTraceNear

		private bool SetLastTrace(PpsTraceItemBase item)
		{
			if (IsLastTraceNear() && // the current trace event is pretty new
				item.Type < lastTrace.Type) // and more important
				return false;

			lastTrace = item;
			return true;
		} // proc SetLastTrace

		private bool UpdateLastTrace()
		{
			if (!IsLastTraceNear())
			{
				lastTrace = null;
				return true;
			}
			else
				return false;
		} // proc UpdateLastTrace

		#endregion

		#region -- Event Handling ---------------------------------------------------------

		private void OnPropertyChanged(string propertyName)
		{
			if (PropertyChanged != null)
				PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
		} // proc OnPropertyChanged

		private void OnCollectionAdded(object item)
		{
			if (CollectionChanged != null)
				CollectionChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item));
		} // proc OnCollectionAdded

		private void OnCollectionRemoved(object item)
		{
			if (CollectionChanged != null)
				CollectionChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item));
		} // proc OnCollectionRemoved

		private void OnCollectionChanged()
		{
			if (CollectionChanged != null)
				CollectionChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
		} // proc OnCollectionChanged

		#endregion

		#region -- IList Member -----------------------------------------------------------

		int IList.Add(object value) { return AppendItem((PpsTraceItemBase)value); }
		void IList.Insert(int index, object value) { throw new NotSupportedException(); }
		void IList.Remove(object value) { throw new NotSupportedException(); }
		void IList.RemoveAt(int index) { throw new NotSupportedException(); }
		bool IList.Contains(object value) { return items.Contains((PpsTraceItemBase)value); }
		int IList.IndexOf(object value) { return items.IndexOf((PpsTraceItemBase)value); }

		bool IList.IsFixedSize { get { return false; } }
		bool IList.IsReadOnly { get { return true; } }

		object IList.this[int index] { get { return items[index]; } set { throw new NotSupportedException(); } }

		#endregion

		#region -- ICollection Member -----------------------------------------------------

		void ICollection.CopyTo(Array array, int index)
		{
			lock (items)
				((ICollection)items).CopyTo(array, index);
		} // proc ICollection.CopyTo

		bool ICollection.IsSynchronized { get { return true; } }
		public object SyncRoot { get { return items; } }

		#endregion

		#region -- IEnumerable Member -----------------------------------------------------

		IEnumerator IEnumerable.GetEnumerator() { return items.GetEnumerator(); }

		#endregion

		/// <summary>Currently, catched events.</summary>
		public int Count { get { lock (items) return items.Count; } }
		/// <summary>The last catched trace event.</summary>
		public PpsTraceItemBase LastTrace
		{
			get { return lastTrace; }
			private set
			{
				if (lastTrace != value)
				{
					lock (items)
						lastTrace = value;

					OnPropertyChanged("LastTrace");
				}
			}
		} // prop LastTrace
	} // class PpsTraceLog

	#endregion
}