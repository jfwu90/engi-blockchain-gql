﻿using GraphQL.Types;

namespace Engi.Substrate.Server.Types;

public class CreateUserArgumentsType : InputObjectGraphType<CreateUserArguments>
{
    public CreateUserArgumentsType()
    {
        Field(x => x.Name)
            .Description("The name of the account.");
        Field(x => x.Mnemonic)
            .Description("The mnemonic used to generate the private key. Must be 12/15/18/21/24 words taken from the English wordlist, or a UTF-8 raw seed of up to 32-bytes.");
        Field(x => x.MnemonicSalt, nullable: true)
            .Description("The salt that will be used to generate the private key in conjunction with the mnemonic. Cannot be used with a raw seed.");
        Field(x => x.Password, nullable: true)
            .Description("The password that will be used to encrypt the private key.");
    }
}