using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TecWare.PPSn.UI
{
	public class RelayCommand : ICommand
	{
		public Action<object> ExecuteAction { get; set; }
		public Predicate<object> CanExecutePredicate { get; set; }

		public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
		{
			if (canExecute == null) canExecute = o => true;
			CanExecutePredicate = canExecute;
			ExecuteAction = execute;
		}

		public bool CanExecute(object parameter)
		{
			return CanExecutePredicate(parameter);
		}

		public void Execute(object parameter)
		{
			ExecuteAction(parameter);
		}

		public event EventHandler CanExecuteChanged;
	}
}
