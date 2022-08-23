﻿using Engi.Substrate.Jobs;
using Engi.Substrate.Keys;
using GraphQL;
using GraphQL.Types;

namespace Engi.Substrate.Server.Types;

public class EngiMutations : ObjectGraphType
{
    private readonly IServiceProvider serviceProvider;

    public EngiMutations(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider;

        Field<UserType>("createUser")
            .Argument<NonNullGraphType<CreateUserArgumentsGraphType>>("user")
            .Resolve(context =>
            {
                var input = context.GetValidatedArgument<CreateUserArguments>("user");

                return CreateUser(input);
            });

        Field<StringGraphType>("balanceTransfer")
            .Argument<NonNullGraphType<BalanceTransferArgumentsGraphType>>("transfer")
            .ResolveAsync(async context =>
            {
                var input = context.GetValidatedArgument<BalanceTransferArguments>("transfer");
                var chainState = await GetLatestChainState();

                return await BalanceTransferAsync(chainState, input);
            });

        Field<StringGraphType>("createJob")
            .Argument<NonNullGraphType<CreateJobArgumentsGraphType>>("job")
            .ResolveAsync(async context =>
            {
                var input = context.GetValidatedArgument<CreateJobArguments>("job");
                var chainState = await GetLatestChainState();

                return await CreateJobAsync(chainState, input);
            });
    }

    private User CreateUser(CreateUserArguments input)
    {
        var sr25519Pair = KeypairFactory.CreateFromAny(input.Mnemonic, input.MnemonicSalt);
        
        bool isEncrypted = input.Password != null;

        var pkcs8 = !isEncrypted
            ? sr25519Pair.ExportToPkcs8() 
            : sr25519Pair.ExportToPkcs8(input.Password!);

        var address = Address.From(sr25519Pair.PublicKey);

        return new User
        {
            Encoded = Convert.ToBase64String(pkcs8),
            Name = input.Name,
            Address = address.Id,
            Metadata = new AccountMetadata
            {
                Content = new[] { "pkcs8", "sr25519" },
                Type = isEncrypted ? new[] { "scrypt", "xsalsa20-poly1305" } : new[] { "none" },
                Version = 3
            }
        };
    }

    private async Task<string> BalanceTransferAsync(
        ChainState chainState, 
        BalanceTransferArguments input)
    {
        var sender = KeypairFactory.CreateFromAny(input.SenderSecret);
        var dest = Address.From(input.RecipientAddress);

        var client = serviceProvider.GetRequiredService<SubstrateClient>();

        var account = await client.GetSystemAccountAsync(sender.Address);

        return await client.BalanceTransferAsync(
            chainState, sender, account, dest, input.Amount, ExtrinsicEra.Immortal, input.Tip);
    }

    private async Task<string> CreateJobAsync(
        ChainState chainState,
        CreateJobArguments args)
    {
        var sender = KeypairFactory.CreateFromAny(args.SenderSecret);

        var client = serviceProvider.GetRequiredService<SubstrateClient>();

        var account = await client.GetSystemAccountAsync(sender.Address);

        return await client.CreateJobAsync(chainState, sender, account, args);
    }

    private async Task<ChainState> GetLatestChainState()
    {
        using var scope = serviceProvider.CreateScope();

        var observers = scope.ServiceProvider
            .GetServices<IChainObserver>()
            .ToArray();

        var chainSnapshotObserver = observers.OfType<ChainSnapshotObserver>().Single();
        var headObserver = observers.OfType<NewHeadChainObserver>().Single();

        return new()
        {
            Metadata = await chainSnapshotObserver.Metadata,
            Version = await chainSnapshotObserver.Version,
            GenesisHash = await chainSnapshotObserver.GenesisHash,
            LatestFinalizedHeader = headObserver.LastFinalizedHeader!
        };
    }
}

