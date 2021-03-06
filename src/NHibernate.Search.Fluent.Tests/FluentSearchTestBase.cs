﻿using System.Reflection;
using NHibernate.Cache;
using NHibernate.Cfg;
using NHibernate.Dialect;
using NHibernate.Driver;
using NHibernate.Mapping.ByCode;
using NHibernate.Tool.hbm2ddl;
using NUnit.Framework;

namespace NHibernate.Search.Fluent.Tests
{
	[TestFixture]
	public abstract class FluentSearchTestBase
	{
		protected ISession Session;
		protected IFullTextSession SearchSession;

		private ISessionFactory sessionFactory;

		[SetUp]
		public void SetUp()
		{
			var configuration = new Configuration();
			configuration
				.SetProperty(NHibernate.Cfg.Environment.GenerateStatistics, "true")
				.SetProperty(NHibernate.Cfg.Environment.Hbm2ddlAuto, "create-drop")
				.SetProperty(NHibernate.Cfg.Environment.UseQueryCache, "true")
				.SetProperty(NHibernate.Cfg.Environment.CacheProvider, typeof (HashtableCacheProvider).AssemblyQualifiedName)
				.SetProperty(NHibernate.Cfg.Environment.ReleaseConnections, "on_close")
				.SetProperty(NHibernate.Cfg.Environment.Dialect, typeof (SQLiteDialect).AssemblyQualifiedName)
				.SetProperty(NHibernate.Cfg.Environment.ConnectionDriver, typeof (SQLite20Driver).AssemblyQualifiedName)
				.SetProperty(NHibernate.Cfg.Environment.ConnectionString, "Data Source=:memory:;Version=3;New=True;");

			var assembly = Assembly.GetExecutingAssembly();

			var modelMapper = new ModelMapper();
			modelMapper.AddMappings(assembly.GetTypes());
			var hbms = modelMapper.CompileMappingForAllExplicitlyAddedEntities();
			configuration.AddDeserializedMapping(hbms, assembly.GetName().Name);

			ConfigureSearch(configuration);

			sessionFactory = configuration.BuildSessionFactory();

			Session = sessionFactory.OpenSession();
			SearchSession = Search.CreateFullTextSession(Session);

			new SchemaExport(configuration)
				.Execute(false, true, false, Session.Connection, null);

			AfterSetup();
		}

		protected virtual void AfterSetup()
		{
		}

		protected virtual void ConfigureSearch(Configuration cfg) { }

		[TearDown]
		public void TearDown()
		{
			Cleanup();
			if (sessionFactory != null)
				sessionFactory.Dispose();
		}

		protected virtual void Cleanup()
		{
		}
	}
}