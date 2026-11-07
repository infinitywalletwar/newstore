// One PostgreSQL and one Kafka container for every test class in this assembly; classes get their own database from PostgreSQL.
[assembly: AssemblyFixture(typeof(NewStore.TestSupport.PostgresServer))]
[assembly: AssemblyFixture(typeof(NewStore.TestSupport.KafkaServer))]
