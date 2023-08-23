using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.IdentityManagement;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Engi.Substrate;
using Engi.Substrate.Indexing;
using Engi.Substrate.Jobs;
using Engi.Substrate.Observers;
using Engi.Substrate.Server;
using Engi.Substrate.Server.Async;
using Engi.Substrate.Server.Authentication;
using Engi.Substrate.Server.Email;
using Engi.Substrate.Server.Github;
using Engi.Substrate.Server.HealthChecks;
using Engi.Substrate.Server.Types.Authentication;
using GraphQL;
using GraphQL.Validation;
using GraphQL.Server.Transports.AspNetCore;
using GraphQL.SystemTextJson;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Polly;
using Raven.Client.Documents.Conventions;

var builder = WebApplication.CreateBuilder(args);

var applicationSection = builder.Configuration.GetRequiredSection("Application");
var engiSection = builder.Configuration.GetRequiredSection("Engi");

builder.WebHost.UseSentry(options =>
{
    options.SampleRate = 0.25f;
});

// services config

builder.Services.AddCors();
builder.Services.AddHealthChecks()
    // background services
    .AddBackgroundServiceHealthCheck<ChainObserverBackgroundService>(HealthStatus.Unhealthy)
    .AddBackgroundServiceHealthCheck<EngineResponseDequeueService>(HealthStatus.Unhealthy)
    // subs
    .AddRavenSubscriptionHealthCheck<ConsistencyCheckService, ConsistencyCheckCommand>(HealthStatus.Degraded)
    .AddRavenSubscriptionHealthCheck<DistributeCodeService, DistributeCodeCommand>(HealthStatus.Unhealthy)
    .AddRavenSubscriptionHealthCheck<EmailDispatchCommandProcessor, EmailDispatchCommand>(HealthStatus.Degraded)
    .AddRavenSubscriptionHealthCheck<JobAttemptQueueingService, JobAttemptedSnapshot>(HealthStatus.Unhealthy)
    .AddRavenSubscriptionHealthCheck<JobCompletedInitiateCodeDistributionService, JobSnapshot>(HealthStatus.Unhealthy)
    .AddRavenSubscriptionHealthCheck<IndexingBackgroundService, ExpandedBlock>(HealthStatus.Unhealthy)
    .AddRavenSubscriptionHealthCheck<QueueEngineRequestCommandService, QueueEngineRequestCommand>(HealthStatus.Unhealthy)
    .AddRavenSubscriptionHealthCheck<RetrieveGithubReadmesService, JobSnapshot>(HealthStatus.Degraded)
    .AddRavenSubscriptionHealthCheck<SolveJobService, SolveJobCommand>(HealthStatus.Unhealthy)
    // indexes
    .AddRavenIndexHealthCheck<BlockIndex>()
    .AddRavenIndexHealthCheck<JobIndex>()
    .AddRavenIndexHealthCheck<JobAggregateIndex>()
    .AddRavenIndexHealthCheck<JobUserAggregatesIndex>()
    .AddRavenIndexHealthCheck<SolutionIndex>()
    .AddRavenIndexHealthCheck<TransactionIndex>();

builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

builder.Services.AddOptions<SubstrateClientOptions>()
    .Bind(builder.Configuration.GetRequiredSection("Substrate"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpClient(nameof(SubstrateClient), (serviceProvider, http) =>
    {
        var options = serviceProvider
            .GetRequiredService<IOptions<SubstrateClientOptions>>().Value;

        http.BaseAddress = new Uri(options.HttpUrl);
    })
    .AddTransientHttpErrorPolicy(x => x.WaitAndRetryAsync(3, @try => TimeSpan.FromSeconds(@try)));

builder.Services.AddTransient(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    return new SubstrateClient(httpClientFactory);
});

builder.Services.AddRaven(
    builder.Configuration.GetRequiredSection("Raven"),
    conventions =>
    {
        conventions.ThrowIfQueryPageSizeIsNotSet = true;

        conventions.Serialization = new EngiSerializationConventions();

        conventions.FindCollectionName = type =>
        {
            if (typeof(EmailDispatchCommand).IsAssignableFrom(type))
            {
                return $"{nameof(EmailDispatchCommand)}s";
            }

            return DocumentConventions.DefaultGetCollectionName(type);
        };
    });

// ASP.NET + auth

builder.Services
    .AddControllers()
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.Converters.Add(new InputsJsonConverter());
    });

builder.Services.AddOptions<ApplicationOptions>()
    .Bind(applicationSection)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<EngiOptions>()
    .Bind(engiSection)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddCors(cors =>
{
    var application = applicationSection.Get<ApplicationOptions>();

    cors.AddDefaultPolicy(policy =>
    {
        var corsBuilder = policy
            .AllowAnyMethod()
            .WithHeaders("Authorization", "Content-Type")
            .WithExposedHeaders("Token-Expired")
            .SetPreflightMaxAge(TimeSpan.FromHours(1));

        if (!builder.Environment.IsDevelopment())
        {
            corsBuilder
                .WithOrigins(application.Url)
                .AllowCredentials();
        }
        else
        {
            corsBuilder.AllowAnyOrigin();
        }
    });
});

builder.Services.AddDataProtection()
    .PersistKeysToRaven();

builder.Host.ConfigureHostOptions(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

// GraphQL

builder.Services.AddHttpContextAccessor()
    .AddTransient<IValidationRule, AuthorizationValidationRule>()
    .AddAuthorizationCore(core =>
    {
        core.AddPolicy(PolicyNames.Authenticated, p => p
            .RequireAuthenticatedUser()
            .RequireClaim("role", "User"));
    });

builder.Services.AddGraphQL(graphql => graphql
    .AddSchema<RootSchema>()
    .ConfigureExecutionOptions(options =>
    {
        var logger = options.RequestServices!.GetRequiredService<ILogger<RootSchema>>();

        options.EnableMetrics = builder.Environment.IsDevelopment();

        options.UnhandledExceptionDelegate =
            ctx =>
            {
                logger.LogError(ctx.OriginalException, "Error occurred: {error}",
                    ctx.OriginalException.Message);

                return Task.CompletedTask;
            };
    })
    .AddSystemTextJson()
    .AddErrorInfoProvider(options =>
    {
        // this is required for the custom validation layer
        options.ExposeData = true;
        options.ExposeExceptionDetails = builder.Environment.IsDevelopment();
    })
    .AddUserContextBuilder(context =>
    {
        GraphQLUserContext userContext = new GraphQLUserContext
        {
            User = context.User
        };
        return userContext;
    }));

// email

builder.Services.Configure<EmailOptions>(
    builder.Configuration.GetRequiredSection("Email"));
builder.Services.AddRazorLight(builder.Environment);
builder.Services.AddSendgrid(
    builder.Configuration.GetRequiredSection("Sendgrid"));
builder.Services.AddHostedService<EmailDispatchCommandProcessor>();

// chain/engi

var engiOptions = builder.Configuration
    .GetRequiredSection("Engi")
    .Get<EngiOptions>();

if (engiOptions.DisableChainObserver == false)
{
    builder.Services.AddHostedService<ChainObserverBackgroundService>();
}

builder.Services.AddSingleton<IChainObserver, NewHeadChainObserver>();
builder.Services.AddSingleton<IChainObserver, ChainSnapshotObserver>();
builder.Services.AddScoped<LatestChainStateProvider>();
builder.Services.AddHostedService<IndexingBackgroundService>();
builder.Services.AddHostedService<ConsistencyCheckService>();
builder.Services.AddScoped<TransactionTipCalculator>();
builder.Services.AddTransient<UserCryptographyService>();

builder.Services.AddTransient<GithubClientFactory>();
builder.Services.AddHostedService<DistributeCodeService>();
builder.Services.AddHostedService<JobCompletedInitiateCodeDistributionService>();
builder.Services.AddHostedService<RetrieveGithubReadmesService>();

if(engiOptions.DisableEngineIntegration == false)
{
    builder.Services.AddHostedService<EngineResponseDequeueService>();
    builder.Services.AddHostedService<JobAttemptQueueingService>();
    builder.Services.AddHostedService<QueueEngineRequestCommandService>();
    builder.Services.AddHostedService<SolveJobService>();
}

// aws

builder.Services.AddTransient<Func<Task<AWSCredentials>>>(serviceProvider =>
{
    var cache = serviceProvider.GetRequiredService<IMemoryCache>();
    var awsOptions = serviceProvider.GetRequiredService<IOptions<AwsOptions>>().Value;

    return () => cache.GetOrCreateAsync("sts-assume-role", async e =>
    {
        if (string.IsNullOrEmpty(engiOptions.AssumeRoleArn))
        {
            return FallbackCredentialsFactory.GetCredentials();
        }

        var stsConfig = new AmazonSecurityTokenServiceConfig().Apply(awsOptions);

        var sts = new AmazonSecurityTokenServiceClient(stsConfig);

        var assumedRole = await sts.AssumeRoleAsync(new AssumeRoleRequest
        {
            DurationSeconds = (int)TimeSpan.FromMinutes(30).TotalSeconds,
            RoleSessionName = "graphql",
            RoleArn = engiOptions.AssumeRoleArn
        });

        // expire cache 15 minutes before actually expiring to allow time for processing

        e.AbsoluteExpiration = assumedRole.Credentials.Expiration - TimeSpan.FromMinutes(15);

        return assumedRole.Credentials;
    });
});

// localstack

var awsSection = builder.Configuration.GetRequiredSection("Aws");

builder.Services.AddOptions<AwsOptions>()
    .Bind(awsSection);

builder.Services.PostConfigure<AwsOptions>(aws =>
{
    // just to be safe, override this if not running locally

    if (!builder.Environment.IsDevelopment())
    {
        aws.ServiceUrl = null;
    }
});

if (builder.Environment.IsDevelopment() && engiOptions.DisableEngineIntegration == false)
{
    var awsOptions = awsSection.Get<AwsOptions>();

    builder.Services.PostConfigure<EngiOptions>(engiOptions =>
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")))
        {
            Console.Error.WriteLine("Make sure to set AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY as described in DEVELOPMENT.md.");
        }

        var iam = new AmazonIdentityManagementServiceClient(
            new AmazonIdentityManagementServiceConfig().Apply(awsOptions));

        var sns = new AmazonSimpleNotificationServiceClient(
            new AmazonSimpleNotificationServiceConfig().Apply(awsOptions));

        var sqs = new AmazonSQSClient(
            new AmazonSQSConfig().Apply(awsOptions));

        while (true)
        {
            var topics = sns.ListTopicsAsync().GetAwaiter().GetResult();

            if (topics.Topics.Count == 2)
            {
                break;
            }

            Console.WriteLine($"{topics.Topics.Count}/2 topics found, waiting for 5 sec.");
            Thread.Sleep(5000);
        }

        while (true)
        {
            var queues = sqs.ListQueuesAsync("graphql-engine-").GetAwaiter().GetResult();

            if (queues.QueueUrls.Count == 2)
            {
                break;
            }

            Console.WriteLine($"{queues.QueueUrls.Count}/2 queues found, waiting for 5 sec.");
            Thread.Sleep(5000);
        }

        var iamRole = iam.ListRolesAsync().GetAwaiter().GetResult()
            .Roles
            .First();

        engiOptions.AssumeRoleArn = iamRole.Arn;

        var inTopic = sns.FindTopicAsync("graphql-engine-in-test.fifo").GetAwaiter().GetResult();

        engiOptions.EngineInputTopicArn = inTopic.TopicArn;
        engiOptions.EngineOutputTopicName = "graphql-engine-out-test.fifo";

        var outQueue = sqs.GetQueueUrlAsync("graphql-engine-out-test.fifo").GetAwaiter().GetResult();

        engiOptions.EngineOutputQueueUrl = outQueue.QueueUrl;
    });
}

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = "User.Session";
    options.IdleTimeout = TimeSpan.FromSeconds(10);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.AddAuthorization(options =>
{
   options.AddPolicy(PolicyNames.Authenticated, policy => policy.RequireClaim(ClaimTypes.Name));
});

// pipeline

var app = builder.Build();

app.UseWebSockets();

app.UseCors();
app.UseAuthentication();
app.UseRouting();
app.UseAuthorization();
app.UseSession();

app.UseEndpoints(endpoints =>
{
    const string graphQLEndpoint = "/api/graphql";

    endpoints.MapHealthChecks("/api/health", new()
    {
        ResponseWriter = (context, report) => context.Response
            .WriteAsJsonAsync(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() }
            })
    });

    endpoints.MapGraphQL<CustomGraphQLHttpMiddleware>(graphQLEndpoint,
        new GraphQLHttpMiddlewareOptions());
    endpoints.MapGraphQLAltair(options: new()
    {
        GraphQLEndPoint = graphQLEndpoint,
        SubscriptionsEndPoint = graphQLEndpoint
    });
    endpoints.MapGraphQLVoyager(options: new()
    {
        GraphQLEndPoint = graphQLEndpoint
    });
    endpoints.MapControllers();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();
