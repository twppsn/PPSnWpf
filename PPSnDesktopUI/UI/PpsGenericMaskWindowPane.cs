﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

			var templateUri = new Uri(Environment.BaseUri, (string)arguments.GetMemberValue("template"));

			// Lade die Definition
			string sSchema = templateUri.ToString().Replace(".xaml", ".sxml");
			var def = new PpsDataSetDefinitionClient(XDocument.Load(sSchema).Root);
			def.EndInit();
			dataSet = (PpsDataSetClient)def.CreateDataSet();

			string sData = templateUri.ToString().Replace(".xaml", ".dxml");
			dataSet.Read(XDocument.Load(sData).Root);

			dataSet.RegisterUndoSink(undoManager);

			// Lade die Maske
			await base.LoadAsync(arguments);

			await Dispatcher.InvokeAsync(() => OnPropertyChanged("Data"));
		} // proc LoadAsync

		[LuaMember(nameof(CommitEdit))]
		public void CommitEdit()
		{
			foreach (var expr in BindingOperations.GetSourceUpdatingBindings(Control))
				expr.UpdateSource();
		} // proc CommitEdit

		[LuaMember(nameof(UndoManager))]
		public PpsUndoManager UndoManager { get { return undoManager; } }

		[LuaMember(nameof(Data))]
		public PpsDataSet Data { get { return dataSet; } }
	} // class PpsGenericMaskWindowPane

	//Q&D
	public class PpsContentTemplateSelector : DataTemplateSelector
	{
		public override DataTemplate SelectTemplate(object item, DependencyObject container)
		{
			var row = item as PpsDataRow;
			if (row == null)
				return null;
			
			var control = container as ContentPresenter;
			if (control == null)
				return null;

			var r = (DataTemplate)control.FindResource(row.Table.Name);
			return r;
		}
	}
}
