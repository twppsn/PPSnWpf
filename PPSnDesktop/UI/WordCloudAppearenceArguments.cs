using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using WordCloudCalculator.Contract;
using WordCloudCalculator.WordCloudCalculator;
using WordCloudCalculator.WPF;

namespace TecWare.PPSn.UI
{
	class WordCloudAppearenceArguments : IWordCloudAppearenceArguments
	{
		public WordCloudAppearenceArguments()
		{
			WordSizeCalculator = WordSizeCalculatorFactory.FormattedTextWordSizeCalculator();
		}
		public Size PanelSize { get; set; }
		public Range FontSizeRange { get; set; } = new Range(9, 20);
		public Range OpacityRange { get; set; } = new Range(0.6, 1);
		public Func<string, double, Size> WordSizeCalculator { get; set; }
	}
}
