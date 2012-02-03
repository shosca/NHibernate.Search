﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Lucene.Net.Analysis;
using NHibernate.Search.Bridge;
using NHibernate.Search.Fluent.Exceptions;
using NHibernate.Search.Fluent.Mapping.Parts;
using NHibernate.Search.Mapping.Definition;

namespace NHibernate.Search.Fluent.Mapping
{
	using Type = System.Type;

	public interface IDocumentMap : IHasAnalyzer
	{
		Type DocumentType { get; }
		string Name { get; set; }
		MemberInfo IdProperty { get; set; }
		IDictionary<ICustomAttributeProvider, float?> BoostValues { get; }
		IDictionary<ICustomAttributeProvider, Type> Analyzers { get; }
		IList<IClassBridgeDefinition> ClassBridges { get; }
		IDictionary<MemberInfo, IList<IFieldDefinition>> FieldMappings { get; }
	}

	public abstract class DocumentMap<T> : IDocumentMap
	{
		private readonly IDictionary<ICustomAttributeProvider, float?> boostValues;
		private readonly IDictionary<ICustomAttributeProvider, Type> analyzers;
		private readonly IList<ClassBridgePart<T>> classBridges;

		protected DocumentMap()
		{
			Name(typeof (T).Name);

			boostValues = new Dictionary<ICustomAttributeProvider, float?>();
			analyzers = new Dictionary<ICustomAttributeProvider, Type>();
			classBridges = new List<ClassBridgePart<T>>();
		}

		Type IDocumentMap.DocumentType
		{
			get { return typeof (T); }
		}

		string IDocumentMap.Name { get; set; }
		MemberInfo IDocumentMap.IdProperty { get; set; }

		IDictionary<ICustomAttributeProvider, float?> IDocumentMap.BoostValues
		{
			get { return boostValues; }
		}

		IDictionary<ICustomAttributeProvider, Type> IDocumentMap.Analyzers
		{
			get { return analyzers; }
		}

		public IList<IClassBridgeDefinition> ClassBridges
		{
			get { return classBridges.Select(b => b.BridgeDefinition).ToList(); }
		}

		public IDictionary<MemberInfo, IList<IFieldDefinition>> FieldMappings
		{
			get { throw new NotImplementedException(); }
		}

		Type IHasAnalyzer.AnalyzerType
		{
			get
			{
				Type type;
				analyzers.TryGetValue(typeof (T), out type);
				return type;
			}
			set { analyzers[typeof (T)] = value; }
		}

		/// <summary>
		/// Defines the Lucene.NET Index Name.
		/// </summary>
		/// <param name="indexName"></param>
		protected void Name(string indexName)
		{
			(this as IDocumentMap).Name = indexName;
		}

		/// <summary>
		/// Defines the DocumentId Mapping.
		/// </summary>
		/// <param name="property"></param>
		/// <returns></returns>
		protected void Id(Expression<Func<T, object>> property)
		{
			if ((this as IDocumentMap).IdProperty != null)
				throw new FluentMappingException("Id can be set only once");
			(this as IDocumentMap).IdProperty = property.ToPropertyInfo();
		}

		/// <summary>
		/// Sets the boost value
		/// </summary>
		/// <param name="boost"></param>
		protected void Boost(float boost)
		{
			boostValues[typeof (T)] = boost;
		}

		/// <summary>
		/// Defines the Default Analyzer for this mapped class.
		/// </summary>
		/// <returns></returns>
		protected AnalyzerPart<DocumentMap<T>> Analyzer()
		{
			return new AnalyzerPart<DocumentMap<T>>(this);
		}

		/// <summary>
		/// Sets the Analyzer to the given Analyzer type.
		/// Shortcut for .Analyzer().Custom&lt;TAnalyzer&gt;
		/// </summary>
		/// <typeparam name="TAnalyzer"></typeparam>
		/// <returns></returns>
		protected DocumentMap<T> Analyzer<TAnalyzer>() where TAnalyzer : Analyzer, new()
		{
			return this.Analyzer().Custom<TAnalyzer>();
		}

		protected ClassBridgePart<T> Bridge<TBridge>() where TBridge : IFieldBridge
		{
			var newPart = new ClassBridgePart<T>(typeof(TBridge));
			classBridges.Add(newPart);
			return newPart;
		}
	}
}