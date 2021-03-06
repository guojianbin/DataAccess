﻿using System;
using System.Linq;
using System.Data;
using System.Data.Common;
using System.Collections.Generic;
using System.Dynamic;

namespace DbParallel.DataAccess
{
	public enum PropertyNamingConvention { None, PascalCase, CamelCase }

	partial class DbAccess
	{
		#region Dynamic Property Naming Convention

		private PropertyNamingConvention _DynamicPropertyNamingConvention = PropertyNamingConvention.None;
		public PropertyNamingConvention DynamicPropertyNamingConvention
		{
			get { return _DynamicPropertyNamingConvention; }
			set { _DynamicPropertyNamingConvention = value; }
		}

		private Func<string, string> _DynamicPropertyNamingResolver = null;
		public Func<string, string> DynamicPropertyNamingResolver
		{
			get
			{
				if (_DynamicPropertyNamingResolver == null)
					_DynamicPropertyNamingResolver = DefaultDynamicPropertyNamingResolver;

				return _DynamicPropertyNamingResolver;
			}
			set
			{
				_DynamicPropertyNamingResolver = value ?? DefaultDynamicPropertyNamingResolver;
			}
		}

		private string DefaultDynamicPropertyNamingResolver(string fieldName)
		{
			switch (_DynamicPropertyNamingConvention)
			{
				case PropertyNamingConvention.PascalCase:
					return StringUtils.DeunderscoreFieldName(fieldName, false);
				case PropertyNamingConvention.CamelCase:
					return StringUtils.DeunderscoreFieldName(fieldName, true);
				default:
					return fieldName;
			}
		}

		#endregion

		#region BindableDynamicObject.XmlSettings

		private readonly BindableDynamicObject.XmlSettings _DynamicObjectXmlSettings = new BindableDynamicObject.XmlSettings();
		/// <summary>
		/// Gets the BindableDynamicObject.XmlSettings object used to control the behavior of XML serialization.
		/// </summary>
		public BindableDynamicObject.XmlSettings DynamicObjectXmlSettings
		{
			get { return _DynamicObjectXmlSettings; }
		}

		#endregion

		#region Load result sets into dynamic data

		private bool _ReadVisibleFieldsOnly = false;
		public bool ReadVisibleFieldsOnly
		{
			get { return _ReadVisibleFieldsOnly; }
			set { _ReadVisibleFieldsOnly = value; }
		}

		protected string[] GetFieldNames(DbDataReader reader)
		{
			int fieldCount = _ReadVisibleFieldsOnly ? reader.VisibleFieldCount : reader.FieldCount;
			string[] fieldNames = new string[fieldCount];
			string columnName;

			for (int i = 0; i < fieldCount; i++)
			{
				columnName = reader.GetName(i);
				fieldNames[i] = DynamicPropertyNamingResolver(columnName);

				if (string.IsNullOrWhiteSpace(fieldNames[i]))
					throw new ArgumentNullException(string.Format("DynamicPropertyNameOfColumn{0} - \"{1}\"", i, columnName));
			}

			return fieldNames;
		}

		private T CreateExpando<T>(DbDataReader reader, string[] fieldNames) where T : IDictionary<string, object>, new()
		{
			T expandoObject = new T();

			if (fieldNames == null)
				fieldNames = GetFieldNames(reader);

			for (int i = 0; i < fieldNames.Length; i++)
				expandoObject.Add(fieldNames[i], reader.GetColumnValue(i));

			return expandoObject;
		}

		private IEnumerable<BindableDynamicObject> LoadDynamicData<T>(DbDataReader reader) where T : IDictionary<string, object>, new()
		{
			string[] fieldNames = GetFieldNames(reader);

			while (reader.Read())
				yield return new BindableDynamicObject(CreateExpando<T>(reader, fieldNames), _DynamicObjectXmlSettings);
		}

		protected DbParameter ExecuteStoredProcedure(StoredProcedureRequest request, Action<DbDataReader> readAction, out List<DbParameter> outputParameters)
		{
			if (request == null)
				throw new ArgumentNullException("request");
			if (string.IsNullOrWhiteSpace(request.CommandText))
				throw new ArgumentNullException("request.CommandText");

			List<DbParameter> outParameters = null;
			DbParameter returnParameter = null;

			using (DbDataReader reader = CreateReader(request.CommandText, request.CommandTimeout, request.CommandType,
				parameters =>
				{
					parameters.Derive(request.InputParameters);
					var dbParameters = parameters.Command.Parameters.OfType<DbParameter>();

					outParameters = dbParameters.Where(p => (p.Direction == ParameterDirection.InputOutput || p.Direction == ParameterDirection.Output) && !string.IsNullOrEmpty(p.ParameterName)).ToList();
					returnParameter = dbParameters.Where(p => p.Direction == ParameterDirection.ReturnValue).FirstOrDefault();
				}, 0))
			{
				if (readAction != null)
					readAction(reader);
			}

			outputParameters = outParameters;

			return returnParameter;
		}

		protected StoredProcedureResponse ExecuteStoredProcedure<T>(StoredProcedureRequest request) where T : IDictionary<string, object>, new()
		{
			StoredProcedureResponse spResponse = new StoredProcedureResponse(_DynamicObjectXmlSettings);
			List<DbParameter> outputParameters;

			DbParameter returnParameter = ExecuteStoredProcedure(request, reader =>
				{
					bool isFirstResultSetVoid = false;

					do
					{
						spResponse.ResultSets.Add(LoadDynamicData<T>(reader).ToList());

						if (spResponse.ResultSets.Count == 1 && reader.FieldCount == 0)
							isFirstResultSetVoid = true;
					} while (reader.NextResult());

					if (spResponse.ResultSets.Count == 1 && spResponse.ResultSets[0].Count == 0 && isFirstResultSetVoid)
						spResponse.ResultSets.Clear();
				}, out outputParameters);

			if (outputParameters != null)
			{
				T expandoObject = new T();

				foreach (DbParameter op in outputParameters)
					expandoObject.Add(op.ParameterName.TrimParameterPrefix(), op.Value);

				spResponse.OutputParameters = new BindableDynamicObject(expandoObject, _DynamicObjectXmlSettings);
			}

			if (returnParameter != null)
				spResponse.ReturnValue = returnParameter.Value;

			return spResponse;
		}

		public StoredProcedureResponse ExecuteStoredProcedure(StoredProcedureRequest request)
		{
			return ExecuteStoredProcedure<ExpandoObject>(request);
		}

		public object ExecuteStoredProcedure(StoredProcedureRequest request, Action<int> exportResultSetStartTag,
			Action<DbDataReader> exportHeader, Action<DbDataReader> exportRow, Action<int> exportResultSetEndTag,
			IDictionary<string, object> outputParametersContainer, int[] resultSetChoices = null, bool bulkRead = false)
		{
			List<DbParameter> outputParameters;
			bool chooseSpecificResultSets = (resultSetChoices != null && resultSetChoices.Length > 0);
			bool chooseOnlyOneResultSet = (resultSetChoices != null && resultSetChoices.Length == 1);

			DbParameter returnParameter = ExecuteStoredProcedure(request, reader =>
				{
					int resultSetIndex = 0;

					do
					{
						if (chooseSpecificResultSets && !resultSetChoices.Contains(resultSetIndex))
							continue;

						if (exportResultSetStartTag != null)
							exportResultSetStartTag(resultSetIndex);

						if (exportHeader != null)
							exportHeader(reader);

						if (exportRow != null)
							if (bulkRead)
								exportRow(reader);
							else
								while (reader.Read())
									exportRow(reader);

						if (exportResultSetEndTag != null)
							exportResultSetEndTag(resultSetIndex);

						if (chooseOnlyOneResultSet)
							break;
					} while (reader.NextResult() && ++resultSetIndex >= 0);
				}, out outputParameters);

			if (outputParameters != null && outputParametersContainer != null)
				foreach (DbParameter op in outputParameters)
					outputParametersContainer.Add(op.ParameterName.TrimParameterPrefix(), op.Value);

			return (returnParameter == null) ? null : returnParameter.Value;
		}

		public ICollection<string> ListCachedStoredProcedures()
		{
			return DerivedParametersCache.ListStoredProcedures(_Connection);
		}

		public int RemoveCachedStoredProcedures(IEnumerable<string> storedProcedures)
		{
			return DerivedParametersCache.RemoveStoredProcedures(_Connection, storedProcedures);
		}

		public bool RefreshStoredProcedureParameters(string storedProcedureName)
		{
			if (string.IsNullOrWhiteSpace(storedProcedureName))
				throw new ArgumentNullException("storedProcedureName");

			using (DbCommand dbCmd = CreateCommand(storedProcedureName, 0, CommandType.StoredProcedure, null))
			{
				return DerivedParametersCache.DeriveParameters(dbCmd, null, true);
			}
		}

		public int RefreshStoredProcedureParameters(IEnumerable<string> storedProcedureNames)
		{
			int cnt = 0;

			foreach (string spName in storedProcedureNames)
				if (RefreshStoredProcedureParameters(spName))
					cnt++;

			return cnt;
		}

		#endregion

		#region CreateDataAdapter for backward compatibility with some old applications

		public DbDataAdapter CreateDataAdapter(string selectCommandText, int commandTimeout, CommandType commandType, Action<DbParameterBuilder> parametersBuilder, int oraResultSets = 1)
		{
			DbDataAdapter dbDataAdapter = _ProviderFactory.CreateDataAdapter();

			dbDataAdapter.SelectCommand = CreateCommand(selectCommandText, commandTimeout, commandType, parametersBuilder);

			OnReaderExecuting(dbDataAdapter.SelectCommand, oraResultSets);

			return dbDataAdapter;
		}

		public DbDataAdapter CreateDataAdapter(string selectCommandText, Action<DbParameterBuilder> parametersBuilder, int oraResultSets = 1)
		{
			return CreateDataAdapter(selectCommandText, 0, _DefaultCommandType, parametersBuilder, oraResultSets);
		}

		#endregion
	}
}

////////////////////////////////////////////////////////////////////////////////////////////////////
//
//	Copyright 2014 Abel Cheng
//	This source code is subject to terms and conditions of the Apache License, Version 2.0.
//	See http://www.apache.org/licenses/LICENSE-2.0.
//	All other rights reserved.
//	You must not remove this notice, or any other, from this software.
//
//	Original Author:	Abel Cheng <abelcys@gmail.com>
//	Created Date:		2014-12-19
//	Original Host:		http://dbParallel.codeplex.com
//	Primary Host:		http://DataBooster.codeplex.com
//	Updated Host:		https://github.com/DataBooster/DataAccess
//	Change Log:
//	Author				Date			Comment
//
//
//
//
//	(Keep code clean rather than complicated code plus long comments.)
//
////////////////////////////////////////////////////////////////////////////////////////////////////
