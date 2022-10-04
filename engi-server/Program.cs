using Engi.Substrate;
using Engi.Substrate.Server;
using Engi.Substrate.Server.Authentication;
using Engi.Substrate.Server.Email;
using Engi.Substrate.Server.Indexing;
using Engi.Substrate.Server.Types.Authentication;
using Engi.Substrate.Server.Types.Validation;
using GraphQL;
using GraphQL.Instrumentation;
using GraphQL.SystemTextJson;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.IdentityModel.Tokens;
using Polly;

var builder = WebApplication.CreateBuilder(args);

var applicationSection = builder.Configuration.GetRequiredSection("Application");
var engiSection = builder.Configuration.GetRequiredSection("Engi");
var substrateSection = builder.Configuration.GetRequiredSection("Substrate");

builder.WebHost.UseSentry();

// services config

builder.Services.AddCors();

builder.Services.AddHealthChecks();

builder.Services.Configure<SubstrateClientOptions>(substrateSection);

builder.Services.AddHttpClient();
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

builder.Services.AddRaven(builder.Configuration.GetRequiredSection("Raven"));

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

builder.Services.Configure<JwtOptions>(jwtSection);
builder.Services.AddSingleton(jwtOptions);

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

    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = context =>
        {
            if (context.Exception.GetType() == typeof(SecurityTokenExpiredException))
            {
                context.Response.Headers.Add("Token-Expired", "true");
            }

            return Task.CompletedTask;
        },
        OnMessageReceived = context =>
        {
            HttpRequest request = context.Request;

            string token = request.Query["access_token"];

            if (!string.IsNullOrEmpty(token))
            {
                context.Token = token;
            }

            return Task.CompletedTask;
        }
    };

    options.ClaimsIssuer = jwtOptions.Issuer;
    options.TokenValidationParameters = new()
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
        policy.WithOrigins(application.Url)
            .AllowAnyMethod()
            .WithHeaders("Authorization", "Content-Type")
            .WithExposedHeaders("Token-Expired")
            .AllowCredentials()
            .SetPreflightMaxAge(TimeSpan.FromHours(1));
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

        options.Schema!.FieldMiddleware
            .Use(new NoMultipleAuthMutationsMiddleware())
            .Use(new ValidationMiddleware());
    })
    .AddSystemTextJson()
    .AddErrorInfoProvider(options =>
    {
        // this is required for the custom validation layer
        options.ExposeData = true;
        options.ExposeExceptionDetails = builder.Environment.IsDevelopment();
    })
    .AddAuthorizationRule());

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
builder.Services.AddScoped<TransactionTipCalculator>();

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
    endpoints.MapGraphQLAltair(options: new()
    {
        GraphQLEndPoint = graphQLEndpoint
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
