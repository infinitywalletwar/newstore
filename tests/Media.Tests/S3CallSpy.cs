using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.S3;
using Amazon.S3.Model;

namespace NewStore.Media.Tests;

/// <summary>
/// Records the <c>CompleteMultipartUpload</c> requests that reach S3 for one bucket, by upload id. Like <see cref="AbortRefuser"/> it is a
/// handler in the AWS SDK's request pipeline of every S3 client built after <see cref="Register"/> (same internal-type dependency, same
/// reason), and it records only its own bucket. Every client counts, including a test's own, so a test counts its own calls too.
/// </summary>
internal sealed class S3CallSpy(string uniqueName) : IRuntimePipelineCustomizer
{
    private readonly List<string> _completedUploadIds = [];
    private readonly Lock _gate = new();

    public string UniqueName => uniqueName;

    public string? Bucket { get; set; }

    public int CompletesFor(string s3UploadId)
    {
        lock (_gate)
        {
            return _completedUploadIds.Count(id => id == s3UploadId);
        }
    }

    public void Register() => RuntimePipelineCustomizerRegistry.Instance.Register(this);

    public void Unregister() => RuntimePipelineCustomizerRegistry.Instance.Deregister(this);

    public void Customize(Type serviceClientType, RuntimePipeline pipeline)
    {
        if (serviceClientType == typeof(AmazonS3Client))
        {
            pipeline.AddHandlerAfter<Marshaller>(new Handler(this));
        }
    }

    private sealed class Handler(S3CallSpy owner) : PipelineHandler
    {
        public override async Task<T> InvokeAsync<T>(IExecutionContext executionContext)
        {
            if (executionContext.RequestContext.OriginalRequest is CompleteMultipartUploadRequest request && request.BucketName == owner.Bucket)
            {
                lock (owner._gate)
                {
                    owner._completedUploadIds.Add(request.UploadId);
                }
            }

            return await base.InvokeAsync<T>(executionContext);
        }
    }
}
