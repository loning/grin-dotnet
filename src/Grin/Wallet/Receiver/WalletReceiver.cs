using System;
using System.IO;
using System.Net.Http;
using System.Text;
using Common;
using Grin.Core;
using Grin.Core.Core.Block;
using Grin.Core.Core.Build;
using Grin.Core.Core.Transaction;
using Grin.Core.Ser;
using Grin.Keychain.Blind;
using Grin.Keychain.ExtKey;
using Grin.Wallet.Types;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Serilog;

namespace Grin.Wallet.Receiver
{
    /// Component used to receive coins, implements all the receiving end of the
    /// wallet REST API as well as some of the command-line operations.

    public class WalletReceiver
    {
        public WalletReceiver(WalletConfig config, Keychain.Keychain.Keychain keychain)
        {
            this.config = config;
            this.keychain = keychain;
        }

        public WalletConfig config { get; }
        public Keychain.Keychain.Keychain keychain { get; }


        public IActionResult Handle(string partialTxStr)
        {

            var partialTx = JsonConvert.DeserializeObject<PartialTx>(partialTxStr);

            if (partialTx != null)
            {
                receive_json_tx(config, keychain, partialTx);
                return new OkResult();

            }
            return new BadRequestResult();
        }








    /// Receive an already well formed JSON transaction issuance and finalize the
    /// transaction, adding our receiving output, to broadcast to the rest of the
    /// network.
    public static void receive_json_tx(
            WalletConfig config,
            Keychain.Keychain.Keychain keychain,
            PartialTx partialTx
        )
        {
             var (amount, blinding, tx) = PartialTx.read_partial_tx(keychain, partialTx);

            var final_tx = receive_transaction(config, keychain,amount, blinding, tx);


            var tx_hex = HexUtil.to_hex(Ser.ser_vec(final_tx));



            using (var c = new HttpClient())
            {
                var uri = "http://192.168.0.3/v1/pool/push";
                c.PostAsync(uri, new JsonContent(new TxWrapper(){tx_hex=tx_hex}));

            }


            //let url = format!("{}/v1/pool/push", config.check_node_api_http_addr.as_str());
            //let _: () = api::client::post(url.as_str(), &TxWrapper { tx_hex: tx_hex
            //})
            //.map_err(|e| Error::Node(e))?;
        }
        public class JsonContent : StringContent
        {
            public JsonContent(object obj) :
                base(JsonConvert.SerializeObject(obj), Encoding.UTF8, "application/json")
            { }
        }

        // Read wallet data without acquiring the write lock.
        public static (Identifier, uint) retrieve_existing_key(
            WalletConfig config,
            Identifier key_id
        )

        {
            return WalletData.read_wallet(config.data_file_dir, wallet_data =>
            {
                var existing = wallet_data.get_output(key_id);

                if (existing != null)

                {
                    var key_id_2 = existing.key_id.Clone();
                    var derivation = existing.n_child;

                    return (key_id_2, derivation);
                }

                throw new Exception("This should never happen!");
            });


            //    if let Some(existing) = wallet_data.get_output(&key_id_set) 

            //            {

            //        let key_id_set = existing.key_id_set.clone();
            //        let derivation = existing.n_child;
            //        (key_id_set, derivation)
            //    } else {
            //        panic!("should never happen");
            //    }
            //})?;
        }

        public static (Identifier, uint) next_available_key(
            WalletConfig config,
            Keychain.Keychain.Keychain keychain
        )
        {
            var res = WalletData.read_wallet(config.data_file_dir,
                wallet_data =>
                {
                    var root_key_id = keychain.Root_key_id();
                    var derivation = wallet_data.next_child(root_key_id.Clone());
                    var key_id = keychain.Derive_key_id(derivation);
                    return (key_id, derivation);
                });

            return res;
        }

        /// Build a coinbase output and the corresponding kernel
        public static (Output, TxKernel, BlockFees) receive_coinbase(
            WalletConfig config,
            Keychain.Keychain.Keychain keychain,
            BlockFees block_fees
        )
        {
            var root_key_id = keychain.Root_key_id();
            var key_id = block_fees.key_id;


            uint derivation;
            if (key_id != null)
            {
                (key_id, derivation) = retrieve_existing_key(config, key_id);
            }
            else
            {
                (key_id, derivation) = next_available_key(config, keychain);
            }

            // Now acquire the wallet lock and write the new output.
            WalletData.with_wallet(config.data_file_dir, wallet_data =>
            {
                // track the new output and return the stuff needed for reward
                var opd = new OutputData(
                    root_key_id.Clone(),
                    key_id.Clone(),
                    derivation,
                    Consensus.reward(block_fees.fees),
                    OutputStatus.Unconfirmed,
                    0,
                    0,
                    true);

                wallet_data.add_output(opd);

                return opd;
            });


            Log.Debug("Received coinbase and built candidate output - {root_key_id}, {key_id_set}, {derivation}",
                root_key_id,
                key_id,
                derivation
            );

            Log.Debug("block_fees - {block_fees}", block_fees);

            block_fees = block_fees.Clone();
            block_fees.key_id_set(key_id.Clone());

            Log.Debug("block_fees updated - {block_fees}", block_fees);

            var (outd, kern) = Block.Reward_output(keychain, key_id, block_fees.fees);
            return (outd, kern, block_fees);
        }

        /// Builds a full transaction from the partial one sent to us for transfer
        public static Transaction receive_transaction(
            WalletConfig config,
            Keychain.Keychain.Keychain keychain,
            ulong amount,
            BlindingFactor blinding,
            Transaction partial
        )
        {
            var root_key_id = keychain.Root_key_id();


            var (key_id, derivation) = next_available_key(config, keychain);

            // double check the fee amount included in the partial tx
            // we don't necessarily want to just trust the sender
            // we could just overwrite the fee here (but we won't) due to the ecdsa sig
            var fee = Types.Types.tx_fee((uint)partial.inputs.Length, (uint)partial.outputs.Length + 1, null);

            if (fee != partial.fee)
            {
                throw new FeeDisputeException
                (
                    partial.fee,
                    fee
                );
            }

            var out_amount = amount - fee;


            var (tx_final, _) = Build.transaction(new Func<Context, Append>[]
            {

                c => c.initial_tx(partial),
                c => c.with_excess(blinding),
                c => c.output(out_amount, key_id.Clone())
            }

        , keychain);

            // make sure the resulting transaction is valid (could have been lied to on excess).
            tx_final.validate(keychain.Secp);

            // operate within a lock on wallet data

            var opd = new OutputData(
                root_key_id.Clone(),
                key_id.Clone(),
                derivation,
                out_amount,
                OutputStatus.Unconfirmed,
                0,
                0,
                false);

            WalletData.with_wallet(config.data_file_dir,
                walletData =>
                {
                    walletData.add_output(opd);

                    return opd;
                });

            Log.Debug(
                "Received txn and built output - {root_key_id}, {key_id}, {derivation}",
                root_key_id,
                key_id,
                derivation
            );

            return tx_final;
        }


    }
}