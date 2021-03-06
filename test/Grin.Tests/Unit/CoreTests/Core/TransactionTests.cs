﻿using System;
using System.IO;
using Common;
using Grin.CoreImpl.Core.Transaction;
using Grin.CoreImpl.Ser;
using Grin.KeychainImpl;
using Secp256k1Proxy.Pedersen;
using Xunit;

namespace Grin.Tests.Unit.CoreTests.Core
{
    public class TransactionTests : IClassFixture<LoggingFixture>
    {
        [Fact]
        public void Test_kernel_ser_deser()
        {
            var keychain = Keychain.From_random_seed();
            var keyId = keychain.Derive_key_id(1);
            var commit = keychain.Commit(5, keyId);

            // just some bytes for testing ser/deser
            var sig = new byte[] {1, 0, 0, 0, 0, 0, 0, 1};

            var kernel = new TxKernel
            {
                Features = KernelFeatures.DefaultKernel,
                LockHeight = 0,
                Excess = commit,
                ExcessSig = sig,
                Fee = 10
            };

            var stream = new MemoryStream();
            Ser.Serialize(stream, kernel);

            Console.WriteLine("-------");
            Console.WriteLine(stream.ToArray().AsString());
            Console.WriteLine("-------");

            stream.Position = 0;

            var kernel2 = Ser.Deserialize(stream, new TxKernel());
            Assert.Equal(KernelFeatures.DefaultKernel, kernel2.Features);
            Assert.Equal<ulong>(0, kernel2.LockHeight);
            Assert.Equal(commit.Value, kernel2.Excess.Value);
            Assert.Equal(sig, kernel2.ExcessSig);
            Assert.Equal<ulong>(10, kernel2.Fee);

            //// now check a kernel with lock_height serializes/deserializes correctly
            kernel = new TxKernel
            {
                Features = KernelFeatures.DefaultKernel,
                LockHeight = 100,
                Excess = commit,
                ExcessSig = sig,
                Fee = 10
            };
            
            stream = new MemoryStream();
            Ser.Serialize(stream, kernel);

            Console.WriteLine("-------");
            Console.WriteLine(stream.ToArray().AsString());
            Console.WriteLine("-------");

            stream.Position = 0;

            kernel2 = Ser.Deserialize(stream, new TxKernel());
            Assert.Equal(KernelFeatures.DefaultKernel, kernel2.Features);
            Assert.Equal<ulong>(100, kernel2.LockHeight);
            Assert.Equal(commit.Value, kernel2.Excess.Value);
            Assert.Equal(sig, kernel2.ExcessSig);
            Assert.Equal<ulong>(10, kernel2.Fee);
        }

        [Fact]
        public void Test_output_ser_deser()
        {
            var keychain = Keychain.From_random_seed();
            var keyIdSet = keychain.Derive_key_id(1);
            var commit = keychain.Commit(5, keyIdSet);
            var switchCommit = keychain.Switch_commit(keyIdSet);
            var switchCommitHash = SwitchCommitHash.From_switch_commit(switchCommit);
            var msg = ProofMessage.Empty();
            var proof = keychain.Range_proof(5, keyIdSet, commit, msg);

            var outp = new Output { 
                Features= OutputFeatures.DefaultOutput,
                Commit= commit,
                SwitchCommitHash= switchCommitHash,
                Proof= proof
            };

            var stream = new MemoryStream();
            Ser.Serialize(stream, outp);

            Console.WriteLine("-------");
            Console.WriteLine(stream.ToArray().AsString());
            Console.WriteLine("-------");

            stream.Position = 0;

            var dout = Ser.Deserialize(stream, new Output());

            Assert.Equal(OutputFeatures.DefaultOutput, dout.Features);
            Assert.Equal(outp.Commit.Value , dout.Commit.Value);
            Assert.Equal(outp.Proof.Proof, dout.Proof.Proof);
        }

        [Fact]
        public void Test_output_value_recovery()
        {
            var keychain = Keychain.From_random_seed();
            var keyId = keychain.Derive_key_id(1);

            var commit = keychain.Commit(1003, keyId);
            var switchCommit = keychain.Switch_commit(keyId);
            var switchCommitHash = SwitchCommitHash.From_switch_commit(switchCommit);
            var msg = ProofMessage.Empty();
            var proof = keychain.Range_proof(1003, keyId, commit, msg);

            var output = new Output
            {
                Features = OutputFeatures.DefaultOutput,
                Commit = commit,
                SwitchCommitHash = switchCommitHash,
                Proof = proof
            };

            // check we can successfully recover the value with the original blinding factor
            var recoveredValue = output.Recover_value(keychain, keyId);
            Assert.Equal<ulong?>(1003, recoveredValue);

            // check we cannot recover the value without the original blinding factor
            var keyId2 = keychain.Derive_key_id(2);
            var notRecoverable = output.Recover_value(keychain, keyId2);
            Assert.Null(notRecoverable);
        }
    }
}