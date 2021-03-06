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
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TecWare.DE.Data;
using TecWare.DE.Stuff;

namespace TecWare.PPSn.Data
{
	#region -- class PpsSqLiteFilterVisitor -------------------------------------------

	public class PpsSqLiteFilterVisitor : PpsDataFilterVisitorSql
	{
		private readonly IDataColumns columns;

		public PpsSqLiteFilterVisitor(IDataColumns columns)
		{
			this.columns = columns;
		} // ctor

		private IDataColumn FindColumnForUser(string columnToken)
		{
			foreach (var col in columns.Columns)
			{
				if (String.Compare(col.Name, columnToken, StringComparison.OrdinalIgnoreCase) == 0 
					|| String.Compare(col.Attributes.GetProperty("displayName", String.Empty), columnToken, StringComparison.OrdinalIgnoreCase) == 0)
					return col;
			}
			return null;
		} // func FindColumnForUser

		protected override Tuple<string, Type> LookupColumn(string columnToken)
		{
			var col = FindColumnForUser(columnToken);
			return col != null
				? new Tuple<string, Type>(col.Name, col.DataType)
				: null;
		} // func LookupColumn

		protected sealed override string LookupNativeExpression(string key)
				=> "1=1"; // not supported

		protected sealed override string CreateDateString(DateTime value)
				=> "datetime('" + value.ToString("s") + "')";
	} // class PpsSqLiteFilterVisitor

	#endregion
}
