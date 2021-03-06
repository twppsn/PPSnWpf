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
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using AForge.Video;
using AForge.Video.DirectShow;
using Neo.IronLua;
using TecWare.PPSn.Controls;
using TecWare.PPSn.Data;

namespace TecWare.PPSn.UI
{
	/// <summary>
	/// Interaction logic for PpsPicturePane.xaml
	/// </summary>
	public partial class PpsPicturePane : UserControl, IPpsWindowPane
	{
		#region -- Helper Classes -----------------------------------------------------

		#region -- Data Representation ------------------------------------------------

		public class PpsCameraHandler : IEnumerable<PpsAforgeCamera>, INotifyCollectionChanged, IDisposable
		{
			#region ---- Readonly ----------------------------------------------------------------

			private readonly System.Timers.Timer refreshTimer;
			private readonly PpsTraceLog traces;
			private readonly System.Windows.Threading.Dispatcher dispatcher;

			#endregion

			#region ---- Events ------------------------------------------------------------------

			public event NotifyCollectionChangedEventHandler CollectionChanged;

			private void RefreshTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
			{
				// the timer may trigger while initialized||lost is called - rendering the list invalid
				lock (awaitingCameras)
				{
					var localWebCamsCollection = new FilterInfoCollection(AForge.Video.DirectShow.FilterCategory.VideoInputDevice);
					// only camera were initialized, which are not already running (devices) and are not in the process ov initializing (awaitingCameras)
					foreach (var cam in (from AForge.Video.DirectShow.FilterInfo vc in localWebCamsCollection where !((from c in cameras select c.MonikerString).Contains(vc.MonikerString)) where !((from c in awaitingCameras select c.MonikerString).Contains(vc.MonikerString)) select vc))
					{
						awaitingCameras.Add(cam);
						var acam = new PpsAforgeCamera(cam, traces);
						acam.CameraInitialized += (ts, te) =>
						{
							lock (awaitingCameras)
							{
								awaitingCameras.Remove((from tcam in awaitingCameras where tcam.MonikerString == ((PpsAforgeCamera)ts).MonikerString select tcam).First());

								cameras.Add((PpsAforgeCamera)ts);

								dispatcher.Invoke(() => CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, new List<PpsAforgeCamera>() { (PpsAforgeCamera)ts })));

								((PpsAforgeCamera)ts).SnapShot += SnapShot;
							}
						};
						acam.CameraLost += (ts, te) =>
						{
							lock (awaitingCameras)
							{
								// the camera may fail during initialization -> delete from awaiting thus reininitalizing
								var waitingCam = (from tcam in awaitingCameras where tcam.MonikerString == ((PpsAforgeCamera)ts).MonikerString select tcam).FirstOrDefault();
								if (waitingCam != null)
									awaitingCameras.Remove(waitingCam);

								// the camera may fail during runtime -> delete from list of running cameras
								var pos = cameras.IndexOf((PpsAforgeCamera)ts);
								if (pos >= 0)
								{
									cameras.Remove((PpsAforgeCamera)ts);

									dispatcher.Invoke(() => CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, (PpsAforgeCamera)ts, pos)));
								}
								((PpsAforgeCamera)ts).Dispose();
							}
						};
					}
				}
			}

			#endregion

			#region ---- Fields ------------------------------------------------------------------

			// actual working cameras
			private List<PpsAforgeCamera> cameras;
			// cameras where initialization already started
			private List<FilterInfo> awaitingCameras = new List<FilterInfo>();

			#endregion

			#region ---- Constructor/Destructor --------------------------------------------------

			public PpsCameraHandler(PpsTraceLog tracelog)
			{
				this.traces = tracelog;
				this.dispatcher = Application.Current.Dispatcher;

				cameras = new List<PpsAforgeCamera>();
				refreshTimer = new System.Timers.Timer();
				refreshTimer.Elapsed += RefreshTimer_Elapsed;
				refreshTimer.Interval = 1000;
				refreshTimer.AutoReset = true;
				refreshTimer.Start();

				RefreshTimer_Elapsed(null, null);
			}

			~PpsCameraHandler()
			{
				Dispose();
			}

			public void Dispose()
			{
				refreshTimer.Stop();
				refreshTimer.Dispose();
				foreach (var cam in cameras)
					cam.Dispose();
			}

			#endregion

			#region ---- Properties --------------------------------------------------------------

			/// <summary>Event is thrown, when a Camera provides a Snapshot (either requested by the App or by a hardware pushbutton</summary>
			public NewFrameEventHandler SnapShot;

			public IEnumerator<PpsAforgeCamera> GetEnumerator()
			{
				return ((IEnumerable<PpsAforgeCamera>)cameras).GetEnumerator();
			}

			IEnumerator IEnumerable.GetEnumerator()
			{
				return ((IEnumerable<PpsAforgeCamera>)cameras).GetEnumerator();
			}

			#endregion
		}

		public class PpsAforgeCamera : INotifyPropertyChanged, IDisposable
		{
			#region ---- Helper Classes ----------------------------------------------------------

			public class CameraProperty : INotifyPropertyChanged
			{
				#region ---- Readonly ---------------------------------------------------------------

				private readonly AForge.Video.DirectShow.CameraControlProperty property;
				private readonly VideoCaptureDevice device;

				private readonly int minValue;
				private readonly int maxValue;
				private readonly int defaultValue;
				private readonly int stepSize;
				private readonly bool flagable;

				#endregion

				#region ---- Events -----------------------------------------------------------------

				public event PropertyChangedEventHandler PropertyChanged;

				#endregion

				#region ---- Constructor ------------------------------------------------------------

				public CameraProperty(VideoCaptureDevice device, AForge.Video.DirectShow.CameraControlProperty property)
				{
					this.device = device;
					this.property = property;

					device.GetCameraPropertyRange(property, out minValue, out maxValue, out stepSize, out defaultValue, out CameraControlFlags flags);
					this.flagable = flags != AForge.Video.DirectShow.CameraControlFlags.None;
				}

				#endregion

				#region ---- Properties -------------------------------------------------------------

				public string Name => Enum.GetName(typeof(AForge.Video.DirectShow.CameraControlProperty), property);
				public int MinValue => minValue;
				public int MaxValue => maxValue;
				public int DefaultValue => defaultValue;
				public int StepSize => stepSize;
				public bool Flagable => flagable;
				public bool AutomaticValue
				{
					get
					{
						if (!flagable)
							return true;
						device.GetCameraProperty(property, out int tmp, out CameraControlFlags flag);
						return (flag == CameraControlFlags.Auto);
					}
					set
					{
						if (!flagable)
							throw new FieldAccessException();

						// there is no use in setting the flag to manual
						if (value)
							device.SetCameraProperty(property, defaultValue, AForge.Video.DirectShow.CameraControlFlags.Auto);

						PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomaticValue)));
					}
				}
				public int Value
				{
					get
					{
						device.GetCameraProperty(property, out int tmp, out CameraControlFlags flag);
						return tmp;
					}
					set
					{
						device.SetCameraProperty(property, value, AForge.Video.DirectShow.CameraControlFlags.Manual);
						PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomaticValue)));
					}
				}

				#endregion
			}

			#endregion

			#region ---- Readonly ----------------------------------------------------------------

			private const string InitializationComplete = "Camera \"{0}\" initialization successful.";
			private const string InitializationFailed = "Camera \"{0}\" initialization failed.";
			private const string DeviceDenied = "Camera \"{0}\" is currently not useable.";
			private const string DeviceFailed = "Camera \"{0}\" crashed. Error Message: \"{1}\"";
			private const string DeviceLost = "Camera \"{0}\" unplugged or lost.";
			private const string PreviewInsufficient = "Camera \"{0}\" does not support required quality. Trying fallback.";
			private const string PreviewUnavailable = "Camera \"{0}\" does not publish useable resolutions.";
			private const string NoSnapshotCapability = "Camera \"{0}\" does not have Snapshot functionality. Using Framegrabber instead.";

			private readonly IEnumerable<CameraProperty> properties;
			private readonly string name;
			private PpsTraceLog traces;
			private VideoCaptureDevice device;

			#endregion

			#region ---- Events ------------------------------------------------------------------

			public event PropertyChangedEventHandler PropertyChanged;

			#endregion

			#region ---- Fields ------------------------------------------------------------------

			private byte[] preview;
			private VideoCapabilities previewResolution;
			private bool initialized = false;

			#endregion

			#region ---- Constructor -------------------------------------------------------------

			public PpsAforgeCamera(AForge.Video.DirectShow.FilterInfo deviceFilter, PpsTraceLog traceLog, int previewMaxWidth = 800, int previewMinFPS = 15)
			{
				this.traces = traceLog;
				this.name = deviceFilter.Name;

				// initialize the device
				try
				{
					device = new VideoCaptureDevice(deviceFilter.MonikerString);
				}
				catch (Exception)
				{
					traces.AppendText(PpsTraceItemType.Fail, String.Format(DeviceDenied, Name));
					device = null;
					return;
				}

				// attach failure handling
				device.VideoSourceError += (sender, e) => traces.AppendText(PpsTraceItemType.Fail, String.Format(DeviceFailed, Name, e.Description));

				// find the highest snapshot resolution
				var maxSnapshotResolution = (from vc in device.SnapshotCapabilities orderby vc.FrameSize.Width * vc.FrameSize.Height descending select vc).FirstOrDefault();

				// there are cameras without snapshot capability
				if (maxSnapshotResolution != null)
				{
					device.ProvideSnapshots = true;
					device.SnapshotResolution = maxSnapshotResolution;

					// attach the event handler for snapshots
					device.SnapshotFrame += SnapshotEvent;
				}

				// find a preview resolution - according to the requirements
				previewResolution = (from vc in device.VideoCapabilities orderby vc.FrameSize.Width descending where vc.FrameSize.Width <= previewMaxWidth where vc.AverageFrameRate >= previewMinFPS select vc).FirstOrDefault();
				if (previewResolution == null)
				{
					// no resolution to the requirements, try to set the highest possible FPS (best for preview)
					traces.AppendText(PpsTraceItemType.Fail, String.Format(PreviewInsufficient, Name));
					previewResolution = (from vc in device.VideoCapabilities orderby vc.AverageFrameRate descending select vc).FirstOrDefault();

					if (previewResolution == null)
					{
						traces.AppendText(PpsTraceItemType.Fail, String.Format(PreviewUnavailable, Name));
					}
				}

				if (!device.ProvideSnapshots)
				{
					traces.AppendText(PpsTraceItemType.Information, String.Format(NoSnapshotCapability, Name));
					device.VideoResolution = (from vc in device.VideoCapabilities orderby vc.FrameSize.Width * vc.FrameSize.Height descending select vc).First();
				}
				else
				{
					device.VideoResolution = previewResolution;
				}

				// attach the handler for incoming images
				device.NewFrame += PreviewNewframeEvent;
				device.Start();

				// collect the useable Propertys
				properties = new List<CameraProperty>();
				foreach (AForge.Video.DirectShow.CameraControlProperty prop in Enum.GetValues(typeof(AForge.Video.DirectShow.CameraControlProperty)))
				{
					var property = new CameraProperty(device, prop);

					// if a property is changed, the camera is supposely not in AutoMode anymore
					property.PropertyChanged += (sender, e) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomaticSettings)));

					// if MinValue==MaxValue the Property is not changeable by the user - thus will not be shown
					if (property.MinValue != property.MaxValue)
						((List<CameraProperty>)properties).Add(property);
				}

				// event is thrown, if the camera is unplugged, after working
				device.PlayingFinished += (sender, e) =>
				{
					traces.AppendText(PpsTraceItemType.Warning, String.Format(DeviceLost, Name));
					CameraLost.Invoke(this, new EventArgs());
				};

				// timeout is thrown if the camera does not provide an image, five seconds after initialization started
				var timeout = new System.Timers.Timer(5000);
				timeout.Elapsed += (s, e) =>
				{
					if (Preview == null)
					{
						traces.AppendText(PpsTraceItemType.Warning, String.Format(InitializationFailed, Name));
						CameraLost?.Invoke(this, new EventArgs());
						((System.Timers.Timer)s).Dispose();
					}
				};
				timeout.Start();
			}

			#endregion

			#region ---- Methods -----------------------------------------------------------------

			public void MakePhoto()
			{
				if (device.ProvideSnapshots)
				{
					device.SimulateTrigger();
				}
				else
				{
					// remove the regular Frame handler to suppress UI-flickering caused by the changed resolution
					device.NewFrame -= PreviewNewframeEvent;
					// attach the Snaphot event to the regular newFrame
					device.NewFrame += SnapshotEvent;
				}
			}

			public void Dispose()
			{
				device.Stop();
				device.WaitForStop();
			}

			#region ---- Event Handler -----------------------------------------------------------

			private void PreviewNewframeEvent(object sender, NewFrameEventArgs eventArgs)
			{
				// receiving a frame is the evidence that the device is working properly
				if (!initialized)
				{
					CameraInitialized?.Invoke(this, new EventArgs());
					traces.AppendText(PpsTraceItemType.Information, String.Format(InitializationComplete, Name));
				}
				initialized = true;

				using (var ms = new MemoryStream())
				{
					eventArgs.Frame.Save(ms, ImageFormat.Bmp);
					ms.Position = 0;
					preview = ms.ToArray();
				}
				eventArgs.Frame.Dispose();

				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
			}

			private void SnapshotEvent(object sender, NewFrameEventArgs eventArgs)
			{
				SnapShot?.Invoke(this, eventArgs);
				if (!device.ProvideSnapshots)
				{
					// remove the Snapshot handler
					device.NewFrame -= SnapshotEvent;
					// reattach the regular Frame handler
					device.NewFrame += PreviewNewframeEvent;
				}
			}

			#endregion

			#endregion

			#region ---- Properties --------------------------------------------------------------

			public byte[] Preview => preview;

			public string Name => !String.IsNullOrWhiteSpace(name) ? char.ToUpper(name[0]) + name.Substring(1) : "Unbekanntes Gerät";

			public IEnumerable<CameraProperty> Properties => properties;

			public bool AutomaticSettings => properties != null ? (from prop in properties where !prop.AutomaticValue select prop).Count() == 0 : true;

			public NewFrameEventHandler SnapShot;

			public EventHandler CameraLost;

			public EventHandler CameraInitialized;

			public string MonikerString => device.Source;

			#endregion
		}

		public class PpsPecStrokeThickness
		{
			private string name;
			private double thickness;

			public PpsPecStrokeThickness(string Name, double Thickness)
			{
				this.name = Name;
				this.thickness = Thickness;
			}

			public string Name => name;
			public double Size => thickness;
		}

		public class PpsPecStrokeColor
		{
			private string name;
			private Brush brush;

			public PpsPecStrokeColor(string Name, Brush ColorBrush)
			{
				this.name = Name;
				this.brush = ColorBrush;
			}

			public string Name => name;
			public Color Color
			{
				get
				{
					if (brush is SolidColorBrush scb) return scb.Color;
					if (brush is LinearGradientBrush lgb) return lgb.GradientStops.FirstOrDefault().Color;
					if (brush is RadialGradientBrush rgb) return rgb.GradientStops.FirstOrDefault().Color;
					return Colors.Black;
				}
			}
			public Brush Brush => brush;
		}

		public class PpsPecStrokeSettings
		{
			private IEnumerable<PpsPecStrokeColor> colors;
			private IEnumerable<PpsPecStrokeThickness> thicknesses;

			public PpsPecStrokeSettings(IEnumerable<PpsPecStrokeColor> Colors, IEnumerable<PpsPecStrokeThickness> Thicknesses)
			{
				this.colors = Colors;
				this.thicknesses = Thicknesses;
			}

			public IEnumerable<PpsPecStrokeColor> Colors => colors;
			public IEnumerable<PpsPecStrokeThickness> Thicknesses => thicknesses;
		}

		#endregion

		#region -- UnDo/ReDo ------------------------------------------------------------

		/// <summary>This StrokeCollection owns a property if ChangedActions should be traced (ref: https://msdn.microsoft.com/en-US/library/aa972158.aspx )</summary>
		private class PpsDetraceableStrokeCollection : StrokeCollection
		{
			private bool disableTracing = false;

			public PpsDetraceableStrokeCollection(StrokeCollection strokes) : base(strokes)
			{

			}

			/// <summary>If true item changes should not be passed to a UndoManager</summary>
			public bool DisableTracing
			{
				get => disableTracing;
				set => disableTracing = value;
			}
		}

		private class PpsAddStrokeUndoItem : IPpsUndoItem
		{
			private PpsDetraceableStrokeCollection collection;
			private Stroke stroke;

			public PpsAddStrokeUndoItem(PpsDetraceableStrokeCollection collection, Stroke strokeAdded)
			{
				this.collection = collection;
				this.stroke = strokeAdded;
			}

			//<summary>Unused</summary>
			public void Freeze()
			{
				//throw new NotImplementedException();
			}

			public void Redo()
			{
				collection.DisableTracing = true;
				try
				{
					collection.Add(stroke);
				}
				finally
				{
					collection.DisableTracing = false;
				}
			}

			public void Undo()
			{
				collection.DisableTracing = true;
				try
				{
					collection.Remove(stroke);
				}
				finally
				{
					collection.DisableTracing = false;
				}
			}
		}

		private class PpsRemoveStrokeUndoItem : IPpsUndoItem
		{
			private PpsDetraceableStrokeCollection collection;
			private Stroke stroke;

			public PpsRemoveStrokeUndoItem(PpsDetraceableStrokeCollection collection, Stroke strokeAdded)
			{
				this.collection = collection;
				this.stroke = strokeAdded;
			}

			//<summary>Unused</summary>
			public void Freeze()
			{
				//throw new NotImplementedException();
			}

			public void Redo()
			{
				collection.DisableTracing = true;
				try
				{
					collection.Remove(stroke);
				}
				finally
				{
					collection.DisableTracing = false;
				}
			}

			public void Undo()
			{
				collection.DisableTracing = true;
				try
				{
					collection.Add(stroke);
				}
				finally
				{
					collection.DisableTracing = false;
				}
			}
		}

		#endregion

		#endregion

		#region -- Events -------------------------------------------------------------

		/// <summary>
		/// Checks, if the mouse is over an InkStroke and changes the cursor according
		/// </summary>
		/// <param name="sender">InkCanvas</param>
		/// <param name="e"></param>
		private void InkCanvasRemoveHitTest(object sender, MouseEventArgs e)
		{
			var hit = false;
			var pos = e.GetPosition((InkCanvas)sender);
			foreach (var stroke in InkStrokes)
				if (stroke.HitTest(pos))
				{
					hit = true;
					break;
				}
			InkEditCursor = hit ? Cursors.No : Cursors.Cross;
		}

		#endregion

		#region -- Fields -------------------------------------------------------------

		private PpsUndoManager strokeUndoManager;
		private readonly PpsEnvironment environment;
		private List<string> captureSourceNames = new List<string>();

		#endregion

		#region -- Constructor --------------------------------------------------------

		public PpsPicturePane()
		{
			InitializeComponent();

			Resources[PpsEnvironment.WindowPaneService] = this;

			environment = PpsEnvironment.GetEnvironment(this) ?? throw new ArgumentNullException("environment");

			InitializePenSettings();
			InitializeCameras();
			InitializeStrokes();

			AddCommandBindings();

			strokeUndoManager = new PpsUndoManager();

			strokeUndoManager.CollectionChanged += (sender, e) => { PropertyChanged?.Invoke(null, new PropertyChangedEventArgs("RedoM")); PropertyChanged?.Invoke(null, new PropertyChangedEventArgs("UndoM")); };

			SetValue(commandsPropertyKey, new PpsUICommandCollection());

			if (imagesList.Items.Count > 0)
				SelectedAttachment = (IPpsAttachmentItem)imagesList.Items[0];
			else if (CameraEnum.Count() > 0)
				SelectedCamera = CameraEnum.First();
		}

		#endregion

		#region -- Commands -----------------------------------------------------------

		#region ---- CommandBindings ----------------------------------------------------------

		private void AddCommandBindings()
		{
			CommandBindings.Add(
				new CommandBinding(
					EditOverlayCommand,
					async (sender, e) =>
					{
						if (e.Parameter is IPpsAttachmentItem i)
						{
							SelectedAttachment = i;

							// if the previous set failed. the user canceled the operation, so exit
							if (SelectedAttachment != i)
								return;
							
							// request the full-sized image
							var imgData = await i.LinkedObject.GetDataAsync<PpsObjectBlobData>();

							var data = await SelectedAttachment.LinkedObject.GetDataAsync<PpsObjectBlobData>();
							InkStrokes = new PpsDetraceableStrokeCollection(await data.GetOverlayAsync() ?? new StrokeCollection());

							InkStrokes.StrokesChanged += (chgsender, chge) =>
							{
								// tracing is disabled, if a undo/redo action caused the changed event, thus preventing it to appear in the undomanager itself
								if (!InkStrokes.DisableTracing)
								{
									using (var trans = strokeUndoManager.BeginTransaction("Linie hinzugefügt"))
									{
										foreach (var stroke in chge.Added)
											strokeUndoManager.Append(new PpsAddStrokeUndoItem((PpsDetraceableStrokeCollection)GetValue(InkStrokesProperty), stroke));
										trans.Commit();
									}
									using (var trans = strokeUndoManager.BeginTransaction("Linie entfernt"))
									{
										foreach (var stroke in chge.Removed)
											strokeUndoManager.Append(new PpsRemoveStrokeUndoItem((PpsDetraceableStrokeCollection)GetValue(InkStrokesProperty), stroke));
										trans.Commit();
									}
								}
							};
							SetCharmObject(i.LinkedObject);
						}
						strokeUndoManager.Clear();
					}));

			CommandBindings.Add(new CommandBinding(
				ApplicationCommands.Save,
				async (sender, e) =>
				{
					if (SelectedAttachment != null)
					{
						var data = await SelectedAttachment.LinkedObject.GetDataAsync<PpsObjectBlobData>();

						await data.SetOverlayAsync(InkStrokes);
						strokeUndoManager.Clear();
					}

				},
				(sender, e) => e.CanExecute = strokeUndoManager.CanUndo));

			CommandBindings.Add(new CommandBinding(
				ApplicationCommands.Delete,
				async (sender, e) =>
				{
					if (e.Parameter is IPpsAttachmentItem pitem)
					{
						pitem.Remove();
					}
					else if (SelectedAttachment is IPpsAttachmentItem sitem)
					{
						sitem.Remove();
					}
				},
				(sender, e) => e.CanExecute = true));

			AddCameraCommandBindings();

			AddStrokeCommandBindings();
		}

		private void AddCameraCommandBindings()
		{
			CommandBindings.Add(new CommandBinding(
				ApplicationCommands.New,
				(sender, e) =>
				{
					if (SelectedCamera != null)
					{
						SelectedCamera.MakePhoto();
					}
				},
				(sender, e) => e.CanExecute = SelectedCamera != null));

			CommandBindings.Add(new CommandBinding(
				ChangeCameraCommand,
				(sender, e) =>
				{
					SelectedCamera = (PpsAforgeCamera)e.Parameter;
					SetCharmObject(null);
				}));
		}


		private void AddStrokeCommandBindings()
		{
			CommandBindings.Add(new CommandBinding(
				OverlayEditFreehandCommand,
				(sender, e) =>
				{
					InkEditMode = InkCanvasEditingMode.Ink;
				}));

			CommandBindings.Add(new CommandBinding(
				OverlayRemoveStrokeCommand,
				(sender, e) =>
				{
					InkEditMode = InkCanvasEditingMode.EraseByStroke;
				}));

			CommandBindings.Add(new CommandBinding(
				ApplicationCommands.Undo,
				(sender, e) =>
				{
					strokeUndoManager.Undo();
				},
				(sender, e) => e.CanExecute = strokeUndoManager.CanUndo));

			CommandBindings.Add(new CommandBinding(
				ApplicationCommands.Redo,
				(sender, e) =>
				{
					strokeUndoManager.Redo();
				},
				(sender, e) => e.CanExecute = strokeUndoManager.CanRedo));

			CommandBindings.Add(new CommandBinding(
				OverlaySetThicknessCommand,
				(sender, e) =>
				{
					var thickness = (PpsPecStrokeThickness)e.Parameter;

					InkDrawingAttributes.Width = InkDrawingAttributes.Height = (double)thickness.Size;
				}));

			CommandBindings.Add(new CommandBinding(
				OverlaySetColorCommand,
				(sender, e) =>
				{
					var color = (PpsPecStrokeColor)e.Parameter;

					InkDrawingAttributes.Color = color.Color;
				},
				(sender, e) => e.CanExecute = true));
		}

		#region Helper Functions

		/// <summary>
		/// Finds the UIElement of a given type in the childs of another control
		/// </summary>
		/// <param name="t">Type of Control</param>
		/// <param name="parent">Parent Control</param>
		/// <returns></returns>
		private DependencyObject FindChildElement(Type t, DependencyObject parent)
		{
			if (parent.GetType() == t)
				return parent;

			DependencyObject ret = null;
			var i = 0;

			while (ret == null && i < VisualTreeHelper.GetChildrenCount(parent))
			{
				ret = FindChildElement(t, VisualTreeHelper.GetChild(parent, i));
				i++;
			}

			return ret;
		}

		#endregion

		#region UICommands

		private static readonly DependencyPropertyKey commandsPropertyKey = DependencyProperty.RegisterReadOnly(nameof(Commands), typeof(PpsUICommandCollection), typeof(PpsPicturePane), new FrameworkPropertyMetadata(null));
		public static readonly DependencyProperty CommandsProperty = commandsPropertyKey.DependencyProperty;

		public static readonly RoutedUICommand EditOverlayCommand = new RoutedUICommand("EditOverlay", "EditOverlay", typeof(PpsPicturePane));
		public static readonly RoutedUICommand OverlayEditFreehandCommand = new RoutedUICommand("EditFreeForm", "EditFreeForm", typeof(PpsPicturePane));
		public static readonly RoutedUICommand OverlayRemoveStrokeCommand = new RoutedUICommand("EditRubber", "EditRubber", typeof(PpsPicturePane));
		public static readonly RoutedUICommand OverlaySetThicknessCommand = new RoutedUICommand("SetThickness", "Set Thickness", typeof(PpsPicturePane));
		public static readonly RoutedUICommand OverlaySetColorCommand = new RoutedUICommand("SetColor", "Set Color", typeof(PpsPicturePane));
		public readonly static RoutedUICommand ChangeCameraCommand = new RoutedUICommand("ChangeCamera", "ChangeCamera", typeof(PpsPicturePane));

		#endregion

		#endregion

		#region Toolbar

		public PpsUICommandCollection Commands => (PpsUICommandCollection)GetValue(CommandsProperty);

		private void RemoveToolbarCommands()
		{
			Commands.Clear();
		}

		private void AddToolbarCommands()
		{
			RemoveToolbarCommands();

			#region Undo/Redo

			UndoManagerListBox listBox;

			var undoCommand = new PpsUISplitCommandButton()
			{
				Order = new PpsCommandOrder(200, 130),
				DisplayText = "Rückgängig",
				Description = "Rückgängig",
				Image = "undoImage",
				DataContext = this,
				Command = new PpsCommand(
					(args) =>
					{
						strokeUndoManager.Undo();
					},
					(args) => strokeUndoManager?.CanUndo ?? false
				),
				Popup = new System.Windows.Controls.Primitives.Popup()
				{
					Child = listBox = new UndoManagerListBox()
					{
						Style = (Style)Application.Current.FindResource("UndoManagerListBoxStyle")
					}
				}
			};
			listBox.SetBinding(FrameworkElement.DataContextProperty, new Binding("DataContext.UndoM"));

			var redoCommand = new PpsUISplitCommandButton()
			{
				Order = new PpsCommandOrder(200, 140),
				DisplayText = "Wiederholen",
				Description = "Wiederholen",
				Image = "redoImage",
				DataContext = this,
				Command = new PpsCommand(
					(args) =>
					{
						strokeUndoManager.Redo();
					},
					(args) => strokeUndoManager?.CanRedo ?? false
				),
				Popup = new System.Windows.Controls.Primitives.Popup()
				{
					Child = listBox = new UndoManagerListBox()
					{
						Style = (Style)Application.Current.FindResource("UndoManagerListBoxStyle")
					}
				}
			};
			listBox.SetBinding(FrameworkElement.DataContextProperty, new Binding("DataContext.RedoM"));

			Commands.Add(undoCommand);
			Commands.Add(redoCommand);

			#endregion

			#region Strokes

			var penSettingsPopup = new System.Windows.Controls.Primitives.Popup()
			{
				Child = new UserControl()
				{
					Style = (Style)this.FindResource("PPSnStrokeSettingsControlStyle"),
					DataContext = StrokeSettings
				}
			};
			penSettingsPopup.Opened += (sender, e) => { if (SelectedAttachment != null) InkEditMode = InkCanvasEditingMode.Ink; };

			var freeformeditCommandButton = new PpsUISplitCommandButton()
			{
				Order = new PpsCommandOrder(300, 110),
				DisplayText = "Freihand",
				Description = "Kennzeichnungen hinzufügen",
				Image = "freeformeditImage",
				Command = new PpsCommand(
						(args) =>
						{
							InkEditMode = InkCanvasEditingMode.Ink;
						},
						(args) => true
					),
				Popup = penSettingsPopup
			};
			Commands.Add(freeformeditCommandButton);

			var removestrokeCommandButton = new PpsUICommandButton()
			{
				Order = new PpsCommandOrder(300, 120),
				DisplayText = "Löschen",
				Description = "Linienzug entfernen",
				Image = "removestrokeImage",
				Command = new PpsCommand(
						(args) =>
						{
							InkEditMode = InkCanvasEditingMode.EraseByStroke;
						},
						(args) => InkStrokes.Count > 0
					)
			};
			Commands.Add(removestrokeCommandButton);

			#endregion

			#region Misc

			var saveCommandButton = new PpsUICommandButton()
			{
				Order = new PpsCommandOrder(400, 110),
				DisplayText = "Speichern",
				Description = "Bild speichern",
				Image = "floppy_diskImage",
				Command = new PpsCommand(
						(args) =>
						{
							ApplicationCommands.Save.Execute(args, this);
						},
						(args) => ApplicationCommands.Save.CanExecute(args, this)
					)
			};
			Commands.Add(saveCommandButton);

			#endregion
		}

		#endregion

		#endregion

		#region -- IPpsWindowPane -----------------------------------------------------

		public string Title => "Bildeditor";

		private string subTitle;
		public string SubTitle
		{
			get
			{
				if (!String.IsNullOrEmpty(subTitle))
					return subTitle;

				if (originalObject != null)
				{
					subTitle = (string)originalObject["Name"];
					return subTitle;
				}

				var window = Application.Current.Windows.OfType<Window>().FirstOrDefault(c => c.IsActive);
				if (window is PpsWindow ppswindow)
				{
					subTitle = (string)(((PpsObject)((dynamic)ppswindow).CharmObject) ?? originalObject)?["Name"];
					return subTitle;
				}
				return String.Empty;
			}
		}

		public object Control => this;

		public IPpsPWindowPaneControl PaneControl => null;

		public bool IsDirty => false;

		public bool HasSideBar => false;

		public event PropertyChangedEventHandler PropertyChanged;

		public PpsWindowPaneCompareResult CompareArguments(LuaTable otherArguments)
		{
			return PpsWindowPaneCompareResult.Reload;
		}

		public void Dispose()
		{
			CameraEnum.Dispose();
			ResetCharmObject();
		}

		/// <summary>
		/// Loads the content of the panel
		/// </summary>
		/// <param name="args">The LuaTable must at least contain ''environment'' and ''Attachments''</param>
		/// <returns></returns>
		public Task LoadAsync(LuaTable args)
		{
			var environment = (args["Environment"] as PpsEnvironment) ?? PpsEnvironment.GetEnvironment(this);
			//DataContext = environment;

			Attachments = (args["Attachments"] as IPpsAttachments);

			return Task.CompletedTask;
		} // proc LoadAsync

		public Task<bool> UnloadAsync(bool? commit = null)
		{
			if (!LeaveCurrentImage())
				return Task.FromResult(false);

			ResetCharmObject();
			return Task.FromResult(true);
		}

		#endregion

		#region -- Charmbar -----------------------------------------------------------

		/// <summary>variable saving the object, which was loaded before opening the PicturePane</summary>
		private PpsObject originalObject;

		/// <summary>restores the object before loading the PicturePane</summary>
		private void ResetCharmObject()
		{
			if (originalObject == null)
				return;

			var wnd = (PpsWindow)Application.Current.Windows.OfType<Window>().FirstOrDefault(c => c.IsActive);

			((dynamic)wnd).CharmObject = originalObject;
		}

		/// <summary>sets the object of the CharmBar - makes a backup, if it was already set (from the pane requesting the PicturePane)</summary>
		/// <param name="obj">new PpsObject</param>
		private void SetCharmObject(PpsObject obj)
		{
			var wnd = (PpsWindow)Application.Current.Windows.OfType<Window>().FirstOrDefault(c => c.IsActive);

			if (originalObject == null)
				originalObject = ((dynamic)wnd).CharmObject;

			((dynamic)wnd).CharmObject = obj;
		}

		#endregion

		#region -- Methods ------------------------------------------------------------

		#region -- Pen Settings -------------------------------------------------------

		private static LuaTable GetPenColorTable(PpsEnvironment environment)
			=> (LuaTable)environment.GetMemberValue("pictureEditorPenColorTable");

		private static LuaTable GetPenThicknessTable(PpsEnvironment environment)
			=> (LuaTable)environment.GetMemberValue("pictureEditorPenThicknessTable");

		private void InitializePenSettings()
		{
			var StrokeThicknesses = new List<PpsPecStrokeThickness>();
			foreach (var tab in GetPenThicknessTable(environment).ArrayList)
			{
				if (tab is LuaTable lt) StrokeThicknesses.Add(new PpsPecStrokeThickness((string)lt["Name"], (double)lt["Thickness"]));
			}

			var StrokeColors = new List<PpsPecStrokeColor>();
			foreach (var tab in GetPenColorTable(environment).ArrayList)
			{
				if (tab is LuaTable lt) StrokeColors.Add(new PpsPecStrokeColor((string)lt["Name"], (Brush)lt["Brush"]));
			}

			if (StrokeColors.Count == 0)
				environment.Traces.AppendText(PpsTraceItemType.Fail, "Failed to load Brushes for drawing.");
			if (StrokeThicknesses.Count == 0)
				environment.Traces.AppendText(PpsTraceItemType.Fail, "Failed to load Thicknesses for drawing.");

			StrokeSettings = new PpsPecStrokeSettings(StrokeColors, StrokeThicknesses);
		}

		#endregion

		#region -- Hardware / Cameras -------------------------------------------------

		private void InitializeCameras()
		{
			CameraEnum = new PpsCameraHandler(environment.Traces);

			CameraEnum.SnapShot += (s, e) =>
			{
				var path = System.IO.Path.GetTempPath() + DateTime.Now.ToUniversalTime().ToString("yyyy-MM-dd_HHmmss") + ".jpg";

				e.Frame.Save(path, ImageFormat.Jpeg);
				e.Frame.Dispose();
				PpsObject obj = null;
				Dispatcher.Invoke(async () =>
				{
					obj = await IncludePictureAsync(path);

					Attachments.Append(obj);

					File.Delete(path);
					var i = 0;

					// scroll the new item into view - 
					// to find the new item one has to scroll one-by-one, because the itemscontrol is virtualizing so a new item can't be found, because it is not rendered, unless it is near the FOV
					while (i < imagesList.Items.Count && imagesList.Items[i] != obj)
					{
						((ContentPresenter)imagesList.ItemContainerGenerator.ContainerFromIndex(i)).BringIntoView();
						i++;
					}

					LastSnapshot = obj;
				}).AwaitTask();
			};
		}

		#endregion

		#region -- Strokes ------------------------------------------------------------

		private bool LeaveCurrentImage()
		{
			if (SelectedAttachment != null && strokeUndoManager.CanUndo)
				switch (MessageBox.Show("Sie haben ungespeicherte Änderungen!\nMöchten Sie diese vor dem Schließen noch speichern?", "Warnung", MessageBoxButton.YesNoCancel))
				{
					case MessageBoxResult.Yes:
						ApplicationCommands.Save.Execute(null, null);
						SetValue(SelectedAttachmentProperty, null); ;
						return true;
					case MessageBoxResult.No:
						while (strokeUndoManager.CanUndo)
							strokeUndoManager.Undo();
						SetValue(SelectedAttachmentProperty, null); ;
						return true;
					default:
						return false;
				}
			return true;
		}

		private void InitializeStrokes()
		{
			InkStrokes = new PpsDetraceableStrokeCollection(new StrokeCollection());

			InkDrawingAttributes = new DrawingAttributes();
		}

		#endregion

		private async Task<PpsObject> IncludePictureAsync(string imagePath)
		{
			PpsObject obj;

			using (var trans = await environment.MasterData.CreateTransactionAsync(PpsMasterDataTransactionLevel.Write))
			{
				obj = await environment.CreateNewObjectFromFileAsync(imagePath);

				trans.Commit();
			}

			return obj;
		} // proc CapturePicutureAsync 

		private void ShowOnlyObjectImageDataFilter(object sender, FilterEventArgs e)
		{
			e.Accepted =
				e.Item is IPpsAttachmentItem item
				&& item.LinkedObject != null
				&& item.LinkedObject.Typ == PpsEnvironment.AttachmentObjectTyp
				&& item.LinkedObject.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
		} // proc ShowOnlyObjectImageDataFilter

		#endregion

		#region -- Propertys ----------------------------------------------------------

		public IEnumerable<object> UndoM => (from un in strokeUndoManager where un.Type == PpsUndoStepType.Undo orderby un.Index descending select un).ToArray();
		public IEnumerable<object> RedoM => (from un in strokeUndoManager where un.Type == PpsUndoStepType.Redo orderby un.Index select un).ToArray();

		/// <summary>Binding Point for caller to set the shown attachments</summary>
		public IPpsAttachments Attachments
		{
			get { return (IPpsAttachments)GetValue(AttachmentsProperty); }
			set { SetValue(AttachmentsProperty, value); }
		}

		/// <summary>The Attachmnet which is shown in the editor</summary>
		private IPpsAttachmentItem SelectedAttachment
		{
			get { return (IPpsAttachmentItem)GetValue(SelectedAttachmentProperty); }
			set
			{
				if (!LeaveCurrentImage())
					return;
				if (value != null && (IPpsAttachmentItem)GetValue(SelectedAttachmentProperty) == null)
				{
					InkEditMode = InkCanvasEditingMode.Ink;
					AddToolbarCommands();
				}
				SetValue(SelectedAttachmentProperty, value);
				SelectedCamera = null;
			}
		}

		/// <summary>The camera which is shown in the editor</summary>
		private PpsAforgeCamera SelectedCamera
		{
			get { return (PpsAforgeCamera)GetValue(SelectedCameraProperty); }
			set
			{
				if (value != null)
				{
					if (!LeaveCurrentImage())
						return;
					RemoveToolbarCommands();
					SelectedAttachment = null;
				}

				SetValue(SelectedCameraProperty, value);
			}
		}

		private PpsObject LastSnapshot
		{
			get { return (PpsObject)GetValue(LastSnapshotProperty); }
			set { SetValue(LastSnapshotProperty, value); }
		}

		/// <summary>The List of cameras which are known to the system - after one is selected it moves to ChachedCameras</summary>
		private PpsCameraHandler CameraEnum
		{
			get { return (PpsCameraHandler)GetValue(CameraEnumProperty); }
			set { SetValue(CameraEnumProperty, value); }
		}

		/// <summary>The Strokes made on the shown Image</summary>
		private PpsDetraceableStrokeCollection InkStrokes
		{
			get { return (PpsDetraceableStrokeCollection)GetValue(InkStrokesProperty); }
			set { SetValue(InkStrokesProperty, value); }
		}

		/// <summary>The state of the Editor</summary>
		private InkCanvasEditingMode InkEditMode
		{
			get { return (InkCanvasEditingMode)GetValue(InkEditModeProperty); }
			set
			{
				SetValue(InkEditModeProperty, value);
				var t = (InkCanvas)FindChildElement(typeof(InkCanvas), this);
				switch ((InkCanvasEditingMode)value)
				{
					case InkCanvasEditingMode.Ink:
						t.MouseMove -= InkCanvasRemoveHitTest;
						InkEditCursor = Cursors.Pen;
						break;
					case InkCanvasEditingMode.EraseByStroke:
						InkEditCursor = Cursors.Cross;
						t.MouseMove += InkCanvasRemoveHitTest;
						break;
					case InkCanvasEditingMode.None:
						t.MouseMove -= InkCanvasRemoveHitTest;
						InkEditCursor = Cursors.Hand;
						break;
				}
			}
		}

		/// <summary>Binding for the Cursor used by the Editor</summary>
		private Cursor InkEditCursor
		{
			get { return (Cursor)GetValue(InkEditCursorProperty); }
			set { SetValue(InkEditCursorProperty, value); }
		}

		/// <summary>The Binding point for Color and Thickness for the Pen</summary>
		private DrawingAttributes InkDrawingAttributes
		{
			get { return (DrawingAttributes)GetValue(InkDrawingAttributesProperty); }
			set { SetValue(InkDrawingAttributesProperty, value); }
		}

		/// <summary>The Binding point for Color and Thickness possibilities for the Settings Control</summary>
		private PpsPecStrokeSettings StrokeSettings
		{
			get { return (PpsPecStrokeSettings)GetValue(StrokeSettingsProperty); }
			set { SetValue(StrokeSettingsProperty, value); }
		}

		#region DependencyPropertys

		public static readonly DependencyProperty AttachmentsProperty = DependencyProperty.Register(nameof(Attachments), typeof(IPpsAttachments), typeof(PpsPicturePane));

		private readonly static DependencyProperty LastSnapshotProperty = DependencyProperty.Register(nameof(LastSnapshot), typeof(PpsObject), typeof(PpsPicturePane));
		private readonly static DependencyProperty SelectedAttachmentProperty = DependencyProperty.Register(nameof(SelectedAttachment), typeof(IPpsAttachmentItem), typeof(PpsPicturePane));
		private readonly static DependencyProperty SelectedCameraProperty = DependencyProperty.Register(nameof(SelectedCamera), typeof(PpsAforgeCamera), typeof(PpsPicturePane));
		private readonly static DependencyProperty CameraEnumProperty = DependencyProperty.Register(nameof(CameraEnum), typeof(PpsCameraHandler), typeof(PpsPicturePane));
		private readonly static DependencyProperty InkDrawingAttributesProperty = DependencyProperty.Register(nameof(InkDrawingAttributes), typeof(DrawingAttributes), typeof(PpsPicturePane));
		private readonly static DependencyProperty InkStrokesProperty = DependencyProperty.Register(nameof(InkStrokes), typeof(PpsDetraceableStrokeCollection), typeof(PpsPicturePane));
		private readonly static DependencyProperty InkEditModeProperty = DependencyProperty.Register(nameof(InkEditMode), typeof(InkCanvasEditingMode), typeof(PpsPicturePane));
		private readonly static DependencyProperty InkEditCursorProperty = DependencyProperty.Register(nameof(InkEditCursor), typeof(Cursor), typeof(PpsPicturePane));
		private readonly static DependencyProperty StrokeSettingsProperty = DependencyProperty.Register(nameof(StrokeSettings), typeof(PpsPecStrokeSettings), typeof(PpsPicturePane));

		#endregion

		#endregion
	}
}
