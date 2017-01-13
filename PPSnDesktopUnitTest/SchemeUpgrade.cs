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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TecWare.PPSn.Data;
using System.Xml.Linq;
using System.Data.SQLite;

namespace TecWare.PPSn.PpsEnvironment
{
	[TestClass]
	public class SchemeUpgrade
	{
		private enum TestCase
		{
			SameTable,
			AddColumn,
			RemoveColumn,
			ChangeColumnType,
			DropableTable
		}
		
		/// <summary>
		/// Compiles the dataset according to the test case.
		/// </summary>
		/// <param name="testcase"></param>
		/// <returns></returns>
		private PpsDataSetDefinitionDesktop GetMasterDataScheme(TestCase testcase)
		{
			#region -- XElements --
			var column1 = XElement.Parse("<column name=\"Column1\" dataType=\"long\" isPrimary=\"true\" isIdentity=\"true\">" +
														"<meta>" +
														 "<displayName dataType=\"string\">dbo.Knst.Id</displayName>" +
														 "<SqlType dataType=\"System.Data.SqlDbType\">BigInt</SqlType>" +
														 "<MaxLength dataType=\"int\">8</MaxLength>" +
														 "<Precision dataType=\"int\">19</Precision>" +
														 "<Scale dataType=\"int\">0</Scale>" +
														 "<IsNull dataType=\"bool\">false</IsNull>" +
														 "<IsIdentity dataType=\"bool\">true</IsIdentity>" +
														"</meta>" +
													  "</column>");
			var column2 = XElement.Parse("<column name =\"Column2\" dataType=\"string\">" +
														"<meta>" +
														 "<displayName dataType=\"string\">dbo.Knst.Id</displayName>" +
														 "<SqlType dataType=\"System.Data.SqlDbType\">NVarChar</SqlType>" +
														 "<MaxLength dataType=\"int\">8</MaxLength>" +
														 "<Precision dataType=\"int\">19</Precision>" +
														 "<Scale dataType=\"int\">0</Scale>" +
														 "<IsNull dataType=\"bool\">true</IsNull>" +
														 "<IsIdentity dataType=\"bool\">false</IsIdentity>" +
														"</meta>" +
													  "</column>");
			var column2int = XElement.Parse("<column name =\"Column2\" dataType=\"int\">" +
														"<meta>" +
														 "<displayName dataType=\"string\">dbo.Knst.Id</displayName>" +
														 "<SqlType dataType=\"System.Data.SqlDbType\">NVarChar</SqlType>" +
														 "<MaxLength dataType=\"int\">8</MaxLength>" +
														 "<Precision dataType=\"int\">19</Precision>" +
														 "<Scale dataType=\"int\">0</Scale>" +
														 "<IsNull dataType=\"bool\">true</IsNull>" +
														 "<IsIdentity dataType=\"bool\">false</IsIdentity>" +
														"</meta>" +
													  "</column>");
			var column3 = XElement.Parse("<column name =\"Column3\" dataType=\"string\">" +
														"<meta>" +
														 "<Default dataType=\"string\">I am a default value.</Default>" +
														 "<displayName dataType=\"string\">dbo.Knst.Id</displayName>" +
														 "<SqlType dataType=\"System.Data.SqlDbType\">NVarChar</SqlType>" +
														 "<MaxLength dataType=\"int\">8</MaxLength>" +
														 "<Precision dataType=\"int\">19</Precision>" +
														 "<Scale dataType=\"int\">0</Scale>" +
														 "<IsNull dataType=\"bool\">true</IsNull>" +
														 "<IsIdentity dataType=\"bool\">false</IsIdentity>" +
														"</meta>" +
													  "</column>");
			var xMustImportMeta = XElement.Parse("<meta>" +
															  "<mustImport dataType=\"string\">true</mustImport>" +
															 "</meta>");

			#endregion

			switch (testcase)
			{
				case TestCase.SameTable:
					return new PpsDataSetDefinitionDesktop(null, "masterDataSet", new XElement("schema",
																									     new XElement("table",
																											  new XAttribute("name", "Table1"),
																											  xMustImportMeta,
																											  column1,
																											  column2)));
				case TestCase.AddColumn:
					return new PpsDataSetDefinitionDesktop(null, "masterDataSet", new XElement("schema",
																										  new XElement("table",
																											  new XAttribute("name", "Table1"),
																											  xMustImportMeta,
																											  column1,
																											  column2,
																											  column3)));
				case TestCase.RemoveColumn:
					return new PpsDataSetDefinitionDesktop(null, "masterDataSet", new XElement("schema",
																										  new XElement("table",
																											  new XAttribute("name", "Table1"),
																											  xMustImportMeta,
																											  column1)));
				case TestCase.ChangeColumnType:
					return new PpsDataSetDefinitionDesktop(null, "masterDataSet", new XElement("schema",
																										  new XElement("table",
																											  new XAttribute("name", "Table1"),
																											  xMustImportMeta,
																											  column1,
																											  column2int)));
				case TestCase.DropableTable:
					return new PpsDataSetDefinitionDesktop(null, "masterDataSet", new XElement("schema",
																										  new XElement("table",
																											  new XAttribute("name", "Table1"),
																											  column1,
																											  column2)));
			}

			return null;
		}

		/// <summary>
		/// Creates the database for the tests
		/// two colums, int and text
		/// with one row of actual data
		/// </summary>
		/// <returns></returns>
		private SQLiteConnection GetTestDatabase()
		{
			var sqliteDataBase = new SQLiteConnection("Data Source=:memory:;DateTimeKind=Utc;foreign keys=true;new=true;");
			{
				sqliteDataBase.Open();

				using (var sqlite = sqliteDataBase.CreateCommand())
				{
					// initialize the table
					sqlite.CommandText = "CREATE TABLE 'Table1' ( [Column1] INTEGER PRIMARY KEY NOT NULL, [Column2] TEXT NULL);";
					sqlite.ExecuteNonQuery();
					sqlite.CommandText = "CREATE UNIQUE INDEX 'Table1_Column1_index' ON 'Table1'([Column1]);";
					sqlite.ExecuteNonQuery();
					sqlite.CommandText = "INSERT INTO 'Table1' VALUES (1,'Testtext');";
					sqlite.ExecuteNonQuery();
				}
			}
			return sqliteDataBase;
		}

		/// <summary>
		/// Not really the Hash - but pretty close
		/// </summary>
		/// <returns></returns>
		private string GetDatabaseHash(SQLiteConnection sqliteDatabase)
		{
			var hash = String.Empty;
			using (var sqlite = sqliteDatabase.CreateCommand())
			{
				// Tables
				sqlite.CommandText = "SELECT [name] FROM 'sqlite_master' WHERE [type] = 'table';";
				var reader = sqlite.ExecuteReader();
				hash += "\n--Tables:\n";
				while (reader.Read())
				{
					for (var i = 0; i < reader.FieldCount; i++)
						hash += reader.GetValue(i).ToString() + '\t';
					hash += '\n';
				}
				reader.Close();
				
				// Scheme
				sqlite.CommandText = "PRAGMA table_info('Table1');";
				reader = sqlite.ExecuteReader();
				hash += "--Scheme:\n";
				while (reader.Read())
				{
					for (var i = 0; i < reader.FieldCount; i++)
						hash += reader.GetValue(i).ToString() + '\t';
					hash += '\n';
				}
				reader.Close();

				// Indexes
				sqlite.CommandText = "SELECT [name] FROM 'sqlite_master' WHERE ([type] = 'index' AND [tbl_name] = 'Table1');";
				reader = sqlite.ExecuteReader();
				hash += "--Indexes:\n";
				while (reader.Read())
				{
					for (var i = 0; i < reader.FieldCount; i++)
						hash += reader.GetValue(i).ToString() + '\t';
					hash += '\n';
				}
				reader.Close();

				// Data
				sqlite.CommandText = "SELECT * FROM 'Table1';";
				reader = sqlite.ExecuteReader();
				hash += "--Data:\n";
				while (reader.Read())
				{
					for (var i = 0; i < reader.FieldCount; i++)
						hash += reader.GetValue(i).ToString() + '\t';
					hash += '\n';
				}
			}
			return hash;
		}

		[TestMethod]
		public void PpsMasterDataImportTest_mustImport_Same()
		{
			using (var sqliteDataBase = GetTestDatabase())
			{
				var beforeState = GetDatabaseHash(sqliteDataBase);

				var master = new PpsMasterData(GetMasterDataScheme(TestCase.SameTable), sqliteDataBase);
				try
				{
					master.RefreshMasterDataScheme();
				}
				catch (Exception e)
				{
					Assert.Fail("The Database throw an Exception while being upgraded." + "\n" + e.Message);
				}

				var afterState = GetDatabaseHash(sqliteDataBase);

				Assert.AreEqual(beforeState.Replace("\t\t", "\tnull\t"), afterState.Replace("\t\t", "\tnull\t"));

				using (var rowIdCommand = sqliteDataBase.CreateCommand())
				{
					// if the table was recreated in the process it would have a new rowid...
					rowIdCommand.CommandText = "SELECT [rowid] FROM 'sqlite_master' WHERE ([type] = 'table' AND [name] = 'Table1');";
					Assert.AreEqual((Int64)1, rowIdCommand.ExecuteScalar(), "The Refresh was supposed to do nothing - but it recreated the database. No data lost but anyhow.");
				}
			}
		}

		[TestMethod]
		public void PpsMasterDataImportTest_mustImport_AddColumn()
		{
			using (var sqliteDataBase = GetTestDatabase())
			{
				var beforeState = GetDatabaseHash(sqliteDataBase);

				var master = new PpsMasterData(GetMasterDataScheme(TestCase.AddColumn), sqliteDataBase);
				try
				{
					master.RefreshMasterDataScheme();
				}
				catch (Exception e)
				{
					Assert.Fail("The Database throw an Exception while being upgraded." + "\n" + e.Message);
				}

				var afterState = GetDatabaseHash(sqliteDataBase);

				// there must be one Column more, with a default value
				var expectedText = "\n--Tables:\nTable1\t\n--Scheme:\n0\tColumn1\tINTEGER\t1\t\t1\t\n1\tColumn2\tTEXT\t0\t\t0\t\n2\tColumn3\tTEXT\t0\t'I am a default value.'\t0\t\n--Indexes:\nTable1_Column1_index\t\n--Data:\n1\tTesttext\tI am a default value.\t\n";
				
				Assert.AreEqual(expectedText.Replace("\t\t", "\tnull\t"), afterState.Replace("\t\t", "\tnull\t"));
			}
		}

		[TestMethod]
		public void PpsMasterDataImportTest_mustImport_RemoveColumn()
		{
			using (var sqliteDataBase = GetTestDatabase())
			{
				var beforeState = GetDatabaseHash(sqliteDataBase);

				var master = new PpsMasterData(GetMasterDataScheme(TestCase.RemoveColumn), sqliteDataBase);
				try
				{
					master.RefreshMasterDataScheme();
				}
				catch (Exception e)
				{
					Assert.Fail("The Database throw an Exception while being upgraded." + "\n" + e.Message);
				}

				var afterState = GetDatabaseHash(sqliteDataBase);

				// there must be one Column more, with a default value
				var expectedText = "\n--Tables:\nTable1\t\n--Scheme:\n0\tColumn1\tINTEGER\t1\t\t1\t\n--Indexes:\nTable1_Column1_index\t\n--Data:\n1\t\n";

				Assert.AreEqual(expectedText.Replace("\t\t", "\tnull\t"), afterState.Replace("\t\t", "\tnull\t"));
			}
		}

		[TestMethod]
		public void PpsMasterDataImportTest_mustImport_ChangeColumnType()
		{
			using (var sqliteDataBase = GetTestDatabase())
			{
				var beforeState = GetDatabaseHash(sqliteDataBase);

				var master = new PpsMasterData(GetMasterDataScheme(TestCase.ChangeColumnType), sqliteDataBase);
				try
				{
					master.RefreshMasterDataScheme();
				}
				catch (Exception e)
				{
					Assert.Fail("The Database throw an Exception while being upgraded." + "\n" + e.Message);
				}

				var afterState = GetDatabaseHash(sqliteDataBase);

				// there must be one Column more, with a default value
				var expectedText = "\n--Tables:\nTable1\t\n--Scheme:\n0\tColumn1\tINTEGER\t1\t\t1\t\n1\tColumn2\tINTEGER\t0\t\t0\t\n--Indexes:\nTable1_Column1_index\t\n--Data:\n1\t\t\n";

				Assert.AreEqual(expectedText.Replace("\t\t","\tnull\t"), afterState.Replace("\t\t", "\tnull\t"));
			}
		}

		[TestMethod]
		public void PpsMasterDataImportTest_Dropable()
		{
			using (var sqliteDataBase = GetTestDatabase())
			{
				var beforeState = GetDatabaseHash(sqliteDataBase);

				var master = new PpsMasterData(GetMasterDataScheme(TestCase.DropableTable), sqliteDataBase);
				try
				{
					master.RefreshMasterDataScheme();
				}
				catch (Exception e)
				{
					Assert.Fail("The Database throw an Exception while being upgraded."+"\n"+e.Message);
				}

				var afterState = GetDatabaseHash(sqliteDataBase);

				// there must be one Column more, with a default value
				var expectedText = "\n--Tables:\nTable1\t\n--Scheme:\n0\tColumn1\tINTEGER\t1\t\t1\t\n1\tColumn2\tTEXT\t0\t\t0\t\n--Indexes:\nTable1_Column1_index\t\n--Data:\n";

				Assert.AreEqual(expectedText.Replace("\t\t", "\tnull\t"), afterState.Replace("\t\t", "\tnull\t"));
			}
		}

		[TestMethod]
		public void PpsMasterDataImportTest_TemporaryTableUncreateable()
		{
			using (var sqliteDataBase = GetTestDatabase())
			{
				using (var addTableCommand = sqliteDataBase.CreateCommand())
				{
					addTableCommand.CommandText = "CREATE TABLE 'Table1_temp' ([Column1] INTEGER PRIMARY KEY NOT NULL);";
					addTableCommand.ExecuteNonQuery();
				}

				var beforeState = GetDatabaseHash(sqliteDataBase);

				var master = new PpsMasterData(GetMasterDataScheme(TestCase.RemoveColumn), sqliteDataBase);
				try
				{
					master.RefreshMasterDataScheme();
				}
				catch (Exception)
				{
					return;
				}

				var afterState = GetDatabaseHash(sqliteDataBase);

				Assert.AreEqual(beforeState,afterState, "The database was supposed to throw an Exception and revert to the original state.");
				Assert.Fail("The database was supposed to throw an Exception");
			}
		}
	}
}

