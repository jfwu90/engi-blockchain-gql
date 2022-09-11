﻿using GraphQL.Types;

namespace Engi.Substrate.Server.Types;

public class CreateUserArgumentsGraphType : InputObjectGraphType<CreateUserArguments>
{
    public CreateUserArgumentsGraphType()
    {
        Description = "Arguments for creating an account key pair.";

        Field(x => x.Display)
            .Description("The user's display name.");
        Field(x => x.Email)
            .Description("The user's email.");
        Field(x => x.EncryptedPkcs8Key)
            .Description("The user's key, packaged in a PKCS8 envelope and encrypted (with PKCS1 padding) with ENGI's public key.");
    }
}