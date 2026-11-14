using System.Net;

using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.S3;
using Amazon.S3.Model;

namespace NewStore.Media.Tests;

/// <summary>
/// Makes S3 refuse <c>AbortMultipartUpload</c> for one bucket while it is <see cref="Armed"/>. It adds a handler to the SDK's request
/// pipeline of every S3 client built after <see cref="Register"/>, and the handler answers only for its own bucket, so other test
/// classes that share the process (and Floci) are not affected.
/// </summary>
// This depends on the SDK's internal-by-name pipeline types (RuntimePipeline, PipelineHandler, IExecutionContext, Marshaller in
// Amazon.Runtime.Internal), because the only public hook for a per-client fault is the HttpClientFactory seam that MediaStorage does
// not offer, and a seam added only for tests would be production code. A Dependabot bump of AWSSDK.S3 can break this file: if it does,
// give MediaStorage an HttpClientFactory hook and answer 403 to DELETE ...?uploadId= in a DelegatingHandler instead.
internal sealed class AbortRefuser(string uniqueName) : IRuntimePipelineCustomizer
{
    private int _attempts;

    public string UniqueName => uniqueName;

    public volatile bool Armed;

    public string? Bucket { get; set; }

    /// <summary>How many aborts were refused.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    public void Register() => RuntimePipelineCustomizerRegistry.Instance.Register(this);

    public void Unregister() => RuntimePipelineCustomizerRegistry.Instance.Deregister(this);

    public void Customize(Type serviceClientType, RuntimePipeline pipeline)
    {
        if (serviceClientType == typeof(AmazonS3Client))
        {
            pipeline.AddHandlerAfter<Marshaller>(new Handler(this));
        }
    }

    private sealed class Handler(AbortRefuser owner) : PipelineHandler
    {
        public override async Task<T> InvokeAsync<T>(IExecutionContext executionContext)
        {
            if (owner.Armed && executionContext.RequestContext.OriginalRequest is AbortMultipartUploadRequest request
                && request.BucketName == owner.Bucket)
            {
                Interlocked.Increment(ref owner._attempts);

                // 403 is not retried by the SDK, so each refused abort is one attempt.
                throw new AmazonS3Exception("Refused by the test.", ErrorType.Sender, "AccessDenied", "test", HttpStatusCode.Forbidden);
            }

            return await base.InvokeAsync<T>(executionContext);
        }
    }
}
