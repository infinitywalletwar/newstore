// One Floci container for every test class in this assembly; each test makes its own bucket.
// Media's fixtures also share one PostgreSQL and one Kafka container (below); a class gets its own database and bucket.
[assembly: AssemblyFixture(typeof(NewStore.Media.Tests.S3Server))]
[assembly: AssemblyFixture(typeof(NewStore.TestSupport.PostgresServer))]
[assembly: AssemblyFixture(typeof(NewStore.TestSupport.KafkaServer))]
// The FFmpeg samples, each made once per run when a test first asks for it.
[assembly: AssemblyFixture(typeof(NewStore.Media.Tests.SampleVideos))]
