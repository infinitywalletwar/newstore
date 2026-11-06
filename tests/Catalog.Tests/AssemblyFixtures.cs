// One PostgreSQL container for every test class in this assembly; classes get their own database from it.
[assembly: AssemblyFixture(typeof(NewStore.TestSupport.PostgresServer))]
