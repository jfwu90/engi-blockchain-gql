﻿using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Engi.Substrate.Indexing;
using Engi.Substrate.Jobs;
using Engi.Substrate.Keys;
using Engi.Substrate.Pallets;
using Engi.Substrate.Server;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Raven.Client.Documents;

namespace Engi.Substrate.Playground;

#pragma warning disable 1998

public static class Program
{
    private static readonly string BaseUrl = "http://localhost:5000";

    private static readonly Keypair SudoKeypair = KeypairFactory.CreateFromAny("0x868020ae0687dda7d57565093a69090211449845a7e11453612800b663307246");
    private static readonly Keypair GeorgiosdKeypair = KeypairFactory.CreateFromAny("ridge accuse cotton debate step theory fade bench flock liar seek day");

    private static readonly Keypair[] TestKeypairs =
    {
        KeypairFactory.CreateFromAny("time treat merit corn crystal fiscal banner zoo jacket pulse frog long"),
        KeypairFactory.CreateFromAny("shove matter cousin govern glare space survey congress torch easy girl profit"),
        KeypairFactory.CreateFromAny("vocal awake scan share position often baby example segment skill perfect unique"),
        KeypairFactory.CreateFromAny("build genre people salute buddy trash polar hero purse hire caught tail")
    };

    public static async Task Main()
    {
    }

    private static async Task TryLoginAsync(Keypair keypair)
    {
        string json = $@"
            {{
                ""query"": ""mutation {{
                    auth {{
                        login(args: {{
                            address: \""{keypair.Address}\"",
                            {CreateSignaturePayload(keypair)}
                        }}) {{ 
                            accessToken
                        }}
                    }}
                }}"",
                ""variables"": {{  }},
                ""operationName"": null
            }}
        ";

        var http = new HttpClient();

        var response = await http.PostAsync($"{BaseUrl}/api/graphql", 
            new StringContent(json.Replace("\r\n", string.Empty), Encoding.UTF8, "application/json"));

        Console.WriteLine(await response.Content.ReadAsStringAsync());

        return;
    }

    private static string CreateSignaturePayload(Keypair keypair)
    {
        var now = DateTimeOffset.UtcNow;

        string payload = $"{keypair.Address.Id}|{now.ToUnixTimeMilliseconds()}";

        var signature = keypair.Sign(Encoding.UTF8.GetBytes(payload));

        return $@"
            signature: {{
                signedOn: \""{now.UtcDateTime.ToString("o")}\"",
                value: \""{Hex.GetString0X(signature)}\""
            }}
        ";
    }

    private static void VerifyKeyDecodingInteropWithJS()
    {
        var encryptionCert = X509CertificatesHelper.CertificateFromBase64String("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");
        var privateKey = encryptionCert.GetRSAPrivateKey()!;
        var encryptedData = Convert.FromBase64String("Kr+YFvio1PAZnpueFdvxVYa/h9d0ywLk78wTqlbgWwWMoj9LQ9Pr3XDEZeNdFSmTBpHZWhfLsY0EUnO4eFzWJTNxjxLxDCrHoUEHtHkGWLElLnEz58wA6vG+7AzAQ2t5FATkEev8Vk6RRnOEda7bVIGqXJFm+ttV0w/zHikKBHJ5zK5lDZBVfJUrFF5/hnrDfH6rL6E1s5gzoTfflChp+wsMiDHIaMft3SHIHMdp4hfjkaLH6vUvkg9sbJZBF/TiKI41CG3/uvPIsy0MOULUKjPFJT/qUrAgLQ6lrB8N7I17KA3Ug3diBtq0/ksLyh5+SiYV6jsz+kN99nkkanVfJA==");

        var keypairPkcs8 = privateKey.Decrypt(encryptedData, RSAEncryptionPadding.Pkcs1);

        var keypair = Keypair.FromPkcs8(Convert.FromBase64String(Encoding.UTF8.GetString(keypairPkcs8)));

        Console.WriteLine(keypair.Address.Id);
        Console.WriteLine(GeorgiosdKeypair.Address.Id);
    }

    private static async Task<string> EncryptKeypairWithEngiPublicKey(Keypair keypair)
    {
        var http = new HttpClient();

        var engiPublicKeyData = await http.GetStringAsync($"{BaseUrl}/api/engi/public-key");

        using var stringReader = new StringReader(engiPublicKeyData);
        
        var pemReader = new PemReader(stringReader);
        var publicKeyParameters = (RsaKeyParameters) pemReader.ReadObject();
        
        var e = new Pkcs1Encoding(new RsaEngine());

        e.Init(true, publicKeyParameters);

        var keypairPkcs8 = Encoding.UTF8.GetBytes(Convert.ToBase64String(keypair.ExportToPkcs8()));

        var encrypted = e.ProcessBlock(keypairPkcs8, 0, keypairPkcs8.Length);

        return Convert.ToBase64String(encrypted);
    }

    private static async Task JobWorkflowTestCaseAsync()
    {
        using var store = new DocumentStore
        {
            Urls = new[] { "http://localhost:8080 " },
            Database = "engi-local"
        };

        store.Conventions.Serialization = new EngiSerializationConventions();

        store.Initialize();

        using var session = store.OpenAsyncSession();

        session.Advanced.MaxNumberOfRequestsPerSession = 100000;

        // create a job

        await CreateJobAsync(GeorgiosdKeypair);

        // wait while it is getting indexed

        var jobDocumentChange = await store.Changes()
            .ForDocumentsInCollection<JobSnapshot>().FirstAsync();

        var job = await session.LoadAsync<JobSnapshot>(jobDocumentChange.Id);

        // create attempts

        var attemptIds = new ulong[TestKeypairs.Length];

        for (var index = 0; index < TestKeypairs.Length; index++)
        {
            var keypair = TestKeypairs[index];

            await AttemptJobAsync(job.JobId, keypair);

            var attemptDocumentChange = await store.Changes()
                .ForDocumentsInCollection<JobAttemptedSnapshot>()
                .FirstAsync();

            var attempt = await session.LoadAsync<JobAttemptedSnapshot>(attemptDocumentChange.Id);

            attemptIds[index] = attempt.AttemptId;
        }

        // solve one test each

        ulong solutionIdCounter = 0;

        for (var index = 0; index < TestKeypairs.Length; index++)
        {
            var keypair = TestKeypairs[index];

            await SolveJobAsync(job.JobId, Interlocked.Add(ref solutionIdCounter, 100), SudoKeypair, keypair.Address, tests =>
            {
                tests[index].Result = TestResult.Passed;
                tests[index].FailedResultMessage = null;
            });

            var solutionChange = await store.Changes()
                .ForDocumentsInCollection<SolutionSnapshot>()
                .FirstAsync();

            var solution = await session.LoadAsync<SolutionSnapshot>(solutionChange.Id);
        }

        // finally pick a dude to solve all

        var solver = TestKeypairs[RandomNumberGenerator.GetInt32(0, TestKeypairs.Length)];

        await SolveJobAsync(job.JobId, Interlocked.Add(ref solutionIdCounter, 100), SudoKeypair, solver.Address,
            tests =>
            {
                foreach (var test in tests)
                {
                    test.Result = TestResult.Passed;
                    test.FailedResultMessage = null;
                }
            });

        return;
    }

    private static async Task SolveJobAsync(
        ulong jobId,
        ulong solutionId,
        Keypair sudoer, 
        Address solver,
        Action<TestAttempt[]>? mutateTests = null)
    {
        var client = new SubstrateClient("http://localhost:9933");

        var chainState = await client.GetChainStateAsync();

        var account = await client.GetSystemAccountAsync(sudoer.Address);

        var callExtrinsic = new SolveJobArguments
        {
            SolutionId = solutionId,
            JobId = jobId,
            Attempt = new()
            {
                AttemptId = 100,
                Attempter = solver,
                Tests = new TestAttempt[]
                {
                    new()
                    {
                        Id = "test-1",
                        Result = TestResult.Failed,
                        FailedResultMessage = "Failed 1"
                    },
                    new()
                    {
                        Id = "test-2",
                        Result = TestResult.Failed,
                        FailedResultMessage = "Failed 2"
                    },
                    new()
                    {
                        Id = "test-3",
                        Result = TestResult.Failed,
                        FailedResultMessage = "Failed 3"
                    },
                    new()
                    {
                        Id = "test-4",
                        Result = TestResult.Failed,
                        FailedResultMessage = "Failed 4"
                    }
                }
            },
            Author = solver,
            PatchFileUrl = "https://test.com"
        };

        if (mutateTests != null)
        {
            mutateTests(callExtrinsic.Attempt.Tests);
        }

        var args = new SudoCallArguments(callExtrinsic);

        var result = await client.AuthorSubmitExtrinsicAsync(
            new SignedExtrinsicArguments<SudoCallArguments>(
                SudoKeypair, args, account, ExtrinsicEra.Immortal, chainState, 0), chainState.Metadata);

        return;
    }

    private static async Task ReadJobFromChainStorageAsync()
    {
        var client = new SubstrateClient("http://localhost:9933");

        ulong jobId = 13575614961675953037;

        string snapshotStorageKey = StorageKeys.Jobs.ForJobId(jobId);

        var job = await client.GetStateStorageAsync(snapshotStorageKey,
            reader => JobSnapshot.Parse(reader, new BlockReference()));

        return;
    }

    private static async Task AttemptJobAsync(ulong jobId, Keypair sender)
    {
        var client = new SubstrateClient("http://localhost:9933");

        var chainState = await client.GetChainStateAsync();

        var account = await client.GetSystemAccountAsync(sender.Address);

        var args = new AttemptJobArguments
        {
            JobId = jobId,
            SubmissionPatchFileUrl = $"https://wetransfer.com/{Guid.NewGuid()}"
        };

        string result = await client.AuthorSubmitExtrinsicAsync(
            new SignedExtrinsicArguments<AttemptJobArguments>(sender, args, account, ExtrinsicEra.Immortal, chainState, 0), 
            chainState.Metadata);
        
        return;
    }

    private static async Task CreateJobAsync(Keypair sender, int numberOfTests = 4)
    {
        var client = new SubstrateClient("http://localhost:9933");

        var chainState = await client.GetChainStateAsync();

        var account = await client.GetSystemAccountAsync(sender.Address);

        var args = new CreateJobArguments
        {
            Funding = 5000,
            RepositoryUrl = "https://github.com/ravendb/ravendb",
            BranchName = "master",
            CommitHash = "119158985933033e05de60533d353b7599b0bbab",
            Language = Language.CSharp,
            Name = "Test job",
            Tests = Enumerable.Range(0, numberOfTests)
                .Select(offset => new Test
                {
                    Id = $"test-{offset + 1}",
                    Result = TestResult.Passed,
                    Required = true
                })
                .ToArray(),
            FilesRequirement = new FilesRequirement
            {
                IsEditable = "*.cs",
                IsAddable = "*.cs",
                IsDeletable = "*.cs"
            }
        };

        await client.AuthorSubmitExtrinsicAsync(
            new SignedExtrinsicArguments<CreateJobArguments>(sender, args, account, ExtrinsicEra.Immortal, chainState, 0),
            chainState.Metadata);

        return; // set breakpoint here
    }

    private static async Task<JobSnapshot> GetJobSnapshotAsync(ulong jobId, string blockHash)
    {
        var client = new SubstrateClient(ChainUrl);

        var block = await client.GetChainBlockAsync(blockHash);

        var blockRef = new BlockReference { Number = block.Block.Header.Number };

        string snapshotStorageKey = StorageKeys.Jobs.ForJobId(jobId);

        return (await client.GetStateStorageAsync(snapshotStorageKey,
            reader => JobSnapshot.Parse(reader, blockRef), blockHash))!;
    }

    private static async Task<EventRecord[]> GetSystemEventsAsync(string blockHash)
    {
        var client = new SubstrateClient(ChainUrl);

        var meta = await client.GetStateMetadataAsync();

        var events = await client.GetSystemEventsAsync(blockHash, meta);

        return events;
    }

    private static async Task InspectMetadataAsync()
    {
        var client = new SubstrateClient("http://localhost:9933");

        var metadata = await client.GetStateMetadataAsync();

        return; // set breakpoint here
    }

    private static async Task InspectWestendMetadataAsync()
    {
        var client = new SubstrateClient("https://westend-rpc.polkadot.io");

        var metadata = await client.GetStateMetadataAsync();

        return; // set breakpoint here
    }

    private static async Task ContractCallErc20BalanceOfExampleAsync()
    {
        var client = new SubstrateClient("http://localhost:9933");

        ContractCall call = new ContractCall
        {
            ContractAddress = "5HjZeHL5MPeKRurbg3HuK6PH9L5uAw822EBBNRKr5Xd6GFy2", // comes from upload
            Origin = "5FHneW46xGXgs5mUiveU4sbTyGBzmstUspZC92UhjJM694ty",
            GasLimit = 4999999999999,
            InputData0X = "0x0f755a56" // selector from metadata
                          + Hex.GetString(Address.Parse("5CiPPseXPECbkjWCa6MnjNokrgYjMqmKndv2rSnekmSK2DjL").Raw)
        };

        var response = await client.RpcAsync<ContractCallResponse>("contracts_call", call);

        return;
    }
}
