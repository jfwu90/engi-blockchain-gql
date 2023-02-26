using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Engi.Substrate;
using Engi.Substrate.Indexing;
using Engi.Substrate.Server;
using Engi.Substrate.Server.Async;
using Engi.Substrate.Server.Authentication;
using Engi.Substrate.Server.Email;
using Engi.Substrate.Server.Github;
using Engi.Substrate.Server.Indexing;
using Engi.Substrate.Server.Types.Authentication;
using GraphQL;
using GraphQL.Server.Transports.AspNetCore;
using GraphQL.SystemTextJson;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.IdentityModel.Tokens;
using Polly;
using Raven.Client.Documents.Conventions;

var builder = WebApplication.CreateBuilder(args);

var applicationSection = builder.Configuration.GetRequiredSection("Application");
var engiSection = builder.Configuration.GetRequiredSection("Engi");
var substrateSection = builder.Configuration.GetRequiredSection("Substrate");

builder.WebHost.UseSentry(options =>
{
    options.SampleRate = 0.25f;
});

// services config

builder.Services.AddCors();
builder.Services.AddHealthChecks();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

builder.Services.Configure<SubstrateClientOptions>(substrateSection);
builder.Services.AddHttpClient(nameof(SubstrateClient), http =>
    {
        var options = substrateSection
            .Get<SubstrateClientOptions>();

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

builder.Services.Configure<ApplicationOptions>(applicationSection);
builder.Services.Configure<EngiOptions>(engiSection);

var jwtSection = builder.Configuration.GetRequiredSection("Jwt");
var jwtOptions = jwtSection.Get<JwtOptions>();
var jwtTokenValidationParameters = new TokenValidationParameters
{
    RequireExpirationTime = true,
    RequireAudience = true,
    RequireSignedTokens = true,
    ClockSkew = TimeSpan.FromSeconds(15.0),
    IssuerSigningKey = new RsaSecurityKey(jwtOptions.IssuerSigningKey),
    ValidIssuer = jwtOptions.Issuer,
    ValidAudience = jwtOptions.Audience,
    ValidateAudience = true,
    ValidateIssuer = true,
    ValidateIssuerSigningKey = true,
    ValidateLifetime = true
};

builder.Services.Configure<JwtOptions>(jwtSection);
builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton(jwtTokenValidationParameters);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = AuthenticationSchemes.Jwt;
    options.DefaultChallengeScheme = AuthenticationSchemes.Jwt;
    options.DefaultScheme = AuthenticationSchemes.Jwt;
})
.AddJwtBearer(options =>
{
    options.Audience = jwtOptions.Audience;
    options.ClaimsIssuer = jwtOptions.Issuer;
    options.TokenValidationParameters = jwtTokenValidationParameters;
    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = context =>
        {
            if (context.Exception.GetType() == typeof(SecurityTokenExpiredException))
            {
                context.Response.Headers.Add("Token-Expired", "true");
            }

            return Task.CompletedTask;
        }
    };
})
.AddScheme<SudoApiKeyAuthenticationOptions, SudoApiKeyAuthenticationHandler>(AuthenticationSchemes.ApiKey, options =>
{
    var engiOptions = engiSection.Get<EngiOptions>();

    options.ApiKey = engiOptions.SudoApiKey;
});

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

var authenticatedPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
    .RequireAuthenticatedUser()
    .AddRequirements(new NonSudoAuthorizationRequirement())
    .Build();

builder.Services.AddSingleton<IAuthorizationHandler, NonSudoAuthorizationRequirement>();
builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = authenticatedPolicy;

    options.AddPolicy(PolicyNames.Authenticated, authenticatedPolicy);
    options.AddPolicy(PolicyNames.Sudo,
        builder => builder.AddAuthenticationSchemes(AuthenticationSchemes.ApiKey).RequireRole(Roles.Sudo));
});

builder.Services.AddControllers(options =>
{
    // require auth by default

    options.Filters.Add(new AuthorizeFilter(authenticatedPolicy));
});

builder.Host.ConfigureHostOptions(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

// GraphQL

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
    .AddAuthorizationRule()
    .AddWebSocketAuthentication<JwtWebSocketAuthenticationService>());

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
builder.Services.AddHostedService<RetrieveGithubReadmesService>();

if(engiOptions.DisableEngineIntegration == false)
{
    builder.Services.AddHostedService<EngineResponseDequeueService>();
    builder.Services.AddHostedService<JobAttemptQueueingService>();
    builder.Services.AddHostedService<QueueEngineRequestCommandService>();
    builder.Services.AddHostedService<SolveJobService>();
}

// localstack

var awsSection = builder.Configuration.GetRequiredSection("Aws");

builder.Services.Configure<AwsOptions>(awsSection)
    .PostConfigure<AwsOptions>(aws =>
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
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION")))
        {
            Console.Error.WriteLine("Make sure to set AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY and AWS_DEFAULT_REGION as described in DEVELOPMENT.md.");
        }

        var sns = new AmazonSimpleNotificationServiceClient(new AmazonSimpleNotificationServiceConfig
        {
            ServiceURL = awsOptions.ServiceUrl
        });

        var sqs = new AmazonSQSClient(new AmazonSQSConfig
        {
            ServiceURL = awsOptions.ServiceUrl
        });

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
            var queues = sqs.ListQueuesAsync("engine-").GetAwaiter().GetResult();

            if (queues.QueueUrls.Count == 2)
            {
                break;
            }

            Console.WriteLine($"{queues.QueueUrls.Count}/2 queues found, waiting for 5 sec.");
            Thread.Sleep(5000);
        }

        var inTopic = sns.FindTopicAsync("engine-in").GetAwaiter().GetResult();

        engiOptions.EngineInputTopicArn = inTopic.TopicArn;
        engiOptions.EngineOutputTopicName = "engine-out";

        var outQueue = sqs.GetQueueUrlAsync("engine-out").GetAwaiter().GetResult();

        engiOptions.EngineOutputQueueUrl = outQueue.QueueUrl;
    });
}

// pipeline

var app = builder.Build();

app.UseWebSockets();

app.UseCors();
app.UseAuthentication();
app.UseRouting();
app.UseAuthorization();

app.UseEndpoints(endpoints =>
{
    const string graphQLEndpoint = "/api/graphql";

    endpoints.MapHealthChecks("/api/health");
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
