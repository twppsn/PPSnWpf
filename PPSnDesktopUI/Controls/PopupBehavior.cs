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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace TecWare.PPSn.Controls
{
	#region -- class PpsPopupBehavior ----------------------------------------------------

	/// <summary></summary>
	public static class PpsPopupBehavior
	{
		public static bool GetForceFocus(UIElement element)
			=> (bool)element.GetValue(ForceFocusProperty);

		public static void SetForceFocus(UIElement element, bool value)
			=> element.SetValue(ForceFocusProperty, value);

		public static readonly DependencyProperty ForceFocusProperty =
			DependencyProperty.RegisterAttached("ForceFocus", typeof(bool), typeof(PpsPopupBehavior), new UIPropertyMetadata(false, OnForceFocusPropertyChanged));

		public static void OnForceFocusPropertyChanged(DependencyObject depObj, DependencyPropertyChangedEventArgs args)
		{
			if (depObj is Popup popup)
				popup.Opened += (sender, e) => OnOpened(popup);
		} // proc PropertyChangedCallback

		private static void OnOpened(Popup popup)
		{
			if (!popup.Child.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)))
			{
				popup.Child.Focusable = true;
				FocusManager.SetFocusedElement(popup, popup.Child);
				Keyboard.Focus(popup.Child);
			}
		} // proc OnOpened

	} // class PpsPopupBehavior

	#endregion
}