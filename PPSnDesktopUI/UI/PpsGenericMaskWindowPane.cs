﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Linq;
using Neo.IronLua;
using TecWare.PPSn.Data;

namespace TecWare.PPSn.UI
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Inhalt, welcher aus einem dynamisch geladenen Xaml besteht
	/// und einer Dataset</summary>
	public class PpsGenericMaskWindowPane : PpsGenericWpfWindowPane
	{
		private PpsUndoManager undoManager = new PpsUndoManager();
		private PpsDataSetClient dataSet; // DataSet which is controlled by the mask

		public PpsGenericMaskWindowPane(PpsEnvironment environment)
			: base(environment)
		{
		} // ctor

		public override async Task LoadAsync(LuaTable arguments)
		{
			await Task.Yield();

			// Lade die Definition
			string sSchema = Path.ChangeExtension(XamlFileName, ".sxml");
			var def = new PpsDataSetDefinitionClient(XDocument.Load(sSchema).Root);
			dataSet = (PpsDataSetClient)def.CreateDataSet();

			string sData = Path.ChangeExtension(XamlFileName, ".dxml");
			dataSet.Read(XDocument.Load(sData).Root);

			dataSet.RegisterUndoSink(undoManager);

			// Lade die Maske
			await base.LoadAsync(arguments);

			await Dispatcher.InvokeAsync(() => OnPropertyChanged("Data"));
		} // proc LoadAsync

		[LuaMember("UndoManager")]
		public PpsUndoManager LuaUndoManager { get { return undoManager; } }

		[LuaMember("Data")]
		public PpsDataSet Data { get { return dataSet; } }

		public override string Title { get { return ((GetMemberValue("Title") ?? ((dynamic)dataSet).Caption) ?? base.Title).ToString(); } }
	} // class PpsGenericMaskWindowPane
}
