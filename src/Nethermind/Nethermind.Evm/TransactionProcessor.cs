//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Specs;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Transaction = Nethermind.Core.Transaction;

namespace Nethermind.Evm
{
    public class TransactionProcessor : ITransactionProcessor
    {
        private readonly EthereumEcdsa _ecdsa;
        private readonly ILogger _logger;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly ISpecProvider _specProvider;
        private readonly IVirtualMachine _virtualMachine;

        public TransactionProcessor(
            ISpecProvider? specProvider,
            IStateProvider? stateProvider,
            IStorageProvider? storageProvider,
            IVirtualMachine? virtualMachine,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _virtualMachine = virtualMachine ?? throw new ArgumentNullException(nameof(virtualMachine));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
            _ecdsa = new EthereumEcdsa(specProvider.ChainId, logManager);
        }

        public void CallAndRestore(Transaction transaction, BlockHeader block, ITxTracer txTracer)
        {
            Execute(transaction, block, txTracer, true);
        }

        public void Execute(Transaction transaction, BlockHeader block, ITxTracer txTracer)
        {
            Execute(transaction, block, txTracer, false);
        }

        private void QuickFail(Transaction tx, BlockHeader block, ITxTracer txTracer, string? reason)
        {
            block.GasUsed += tx.GasLimit;
            
            Address recipient = tx.To ?? ContractAddress.From(
                tx.SenderAddress ?? Address.Zero,
                _stateProvider.GetNonce(tx.SenderAddress ?? Address.Zero));
            
            // TODO: this is possibly an unnecessary calculation inside EIP-658
            _stateProvider.RecalculateStateRoot();
            Keccak? stateRoot = _specProvider.GetSpec(block.Number).IsEip658Enabled ? null : _stateProvider.StateRoot;
            if (txTracer.IsTracingReceipt)
                txTracer.MarkAsFailed(recipient, tx.GasLimit, Array.Empty<byte>(), reason ?? "invalid", stateRoot);
        }

        private void Execute(Transaction transaction, BlockHeader block, ITxTracer txTracer, bool isCall)
        {
            bool notSystemTransaction = !transaction.IsSystem();
            bool freeTransaction = transaction.IsSystem() || transaction.IsServiceTransaction;
            bool wasSenderAccountCreatedInsideACall = false;
            
            IReleaseSpec spec = _specProvider.GetSpec(block.Number);
            if (!notSystemTransaction)
            {
                spec = new SystemTransactionReleaseSpec(spec);
            }
            
            UInt256 value = transaction.Value;

            UInt256 feeCap = transaction.FeeCap;
            UInt256 baseFee = block.BaseFee;
            if (baseFee > feeCap && !freeTransaction)
            {
                TraceLogInvalidTx(transaction, "MINER_PREMIUM_IS_NEGATIVE");
                QuickFail(transaction, block, txTracer, "miner premium is negative");
                return;
            }
            
            UInt256 premiumPerGas = (feeCap < baseFee && freeTransaction) ? UInt256.Zero  : UInt256.Min(transaction.GasPremium, feeCap - baseFee);
            UInt256 gasPrice = transaction.CalculateEffectiveGasPrice(spec.IsEip1559Enabled, block.BaseFee);

            long gasLimit = transaction.GasLimit;
            byte[] machineCode = transaction.IsContractCreation ? transaction.Data : null;
            byte[] data = transaction.IsMessageCall ? transaction.Data : Array.Empty<byte>();

            Address? caller = transaction.SenderAddress;
            if (_logger.IsTrace) _logger.Trace($"Executing tx {transaction.Hash}");

            if (caller is null)
            {
                TraceLogInvalidTx(transaction, "SENDER_NOT_SPECIFIED");
                QuickFail(transaction, block, txTracer, "sender not specified");
                return;
            }

            long intrinsicGas = IntrinsicGasCalculator.Calculate(transaction, spec);
            if (_logger.IsTrace) _logger.Trace($"Intrinsic gas calculated for {transaction.Hash}: " + intrinsicGas);

            if (notSystemTransaction)
            {
                if (gasLimit < intrinsicGas)
                {
                    TraceLogInvalidTx(transaction, $"GAS_LIMIT_BELOW_INTRINSIC_GAS {gasLimit} < {intrinsicGas}");
                    QuickFail(transaction, block, txTracer, "gas limit below intrinsic gas");
                    return;
                }

                if (!isCall && gasLimit > block.GetActualGasLimit(spec) - block.GasUsed)
                {
                    TraceLogInvalidTx(transaction,
                        $"BLOCK_GAS_LIMIT_EXCEEDED {gasLimit} > {block.GetActualGasLimit(spec)} - {block.GasUsed}");
                    QuickFail(transaction, block, txTracer, "block gas limit exceeded");
                    return;
                }
            }

            if (!_stateProvider.AccountExists(caller))
            {
                // hacky fix for the potential recovery issue
                if (transaction.Signature != null)
                {
                    transaction.SenderAddress = _ecdsa.RecoverAddress(transaction, !spec.ValidateChainId);
                }

                if (caller != transaction.SenderAddress)
                {
                    if (_logger.IsWarn) _logger.Warn($"TX recovery issue fixed - tx was coming with sender {caller} and the now it recovers to {transaction.SenderAddress}");
                    caller = transaction.SenderAddress;
                }
                else
                {
                    TraceLogInvalidTx(transaction, $"SENDER_ACCOUNT_DOES_NOT_EXIST {caller}");
                    if (isCall || gasPrice == UInt256.Zero)
                    {
                        wasSenderAccountCreatedInsideACall = isCall;
                        _stateProvider.CreateAccount(caller, UInt256.Zero);
                    }
                }
                
                if (caller is null)
                {
                    throw new InvalidDataException(
                        $"Failed to recover sender address on tx {transaction.Hash} when previously recovered sender account did not exist.");
                }
            }

            if (notSystemTransaction)
            {
                UInt256 senderBalance = _stateProvider.GetBalance(caller);
                if (!isCall && (ulong) intrinsicGas * gasPrice + value > senderBalance)
                {
                    TraceLogInvalidTx(transaction, $"INSUFFICIENT_SENDER_BALANCE: ({caller})_BALANCE = {senderBalance}");
                    QuickFail(transaction, block, txTracer, "insufficient sender balance");
                    return;
                }

                if (transaction.Nonce != _stateProvider.GetNonce(caller))
                {
                    TraceLogInvalidTx(transaction, $"WRONG_TRANSACTION_NONCE: {transaction.Nonce} (expected {_stateProvider.GetNonce(caller)})");
                    QuickFail(transaction, block, txTracer, "wrong transaction nonce");
                    return;
                }

                _stateProvider.IncrementNonce(caller);
            }

            UInt256 senderReservedGasPayment = isCall ? UInt256.Zero : (ulong) gasLimit * gasPrice;
            
            _stateProvider.SubtractFromBalance(caller, senderReservedGasPayment, spec);
            _stateProvider.Commit(spec, txTracer.IsTracingState ? txTracer : NullTxTracer.Instance);

            long unspentGas = gasLimit - intrinsicGas;
            long spentGas = gasLimit;

            int stateSnapshot = _stateProvider.TakeSnapshot();
            int storageSnapshot = _storageProvider.TakeSnapshot();

            _stateProvider.SubtractFromBalance(caller, value, spec);
            byte statusCode = StatusCode.Failure;
            TransactionSubstate substate = null;

            Address? recipientOrNull = null;
            try
            {
                Address recipient = transaction.GetRecipient(transaction.IsContractCreation ? _stateProvider.GetNonce(caller) : 0);
                if (transaction.IsContractCreation)
                {
                    if (_stateProvider.AccountExists(recipient))
                    {
                        if (_virtualMachine.GetCachedCodeInfo(recipient, spec).MachineCode.Length != 0 || _stateProvider.GetNonce(recipient) != 0)
                        {
                            if (_logger.IsTrace)
                            {
                                _logger.Trace($"Contract collision at {recipient}"); // the account already owns the contract with the code
                            }

                            throw new TransactionCollisionException();
                        }

                        _stateProvider.UpdateStorageRoot(recipient, Keccak.EmptyTreeHash);
                    }
                }

                if (recipient == null)
                {
                    throw new InvalidDataException("Recipient has not been resolved properly before tx execution");
                }

                recipientOrNull = recipient;
                
                ExecutionEnvironment env = new();
                env.TxExecutionContext = new TxExecutionContext(block, caller, gasPrice);
                env.Value = value;
                env.TransferValue = value;
                env.Caller = caller;
                env.CodeSource = recipient;
                env.ExecutingAccount = recipient;
                env.InputData = data ?? Array.Empty<byte>();
                env.CodeInfo = machineCode == null ? _virtualMachine.GetCachedCodeInfo(recipient, spec) : new CodeInfo(machineCode);

                ExecutionType executionType = transaction.IsContractCreation ? ExecutionType.Create : ExecutionType.Call;
                using (EvmState state = new(unspentGas, env, executionType, true, false))
                {
                    if (spec.UseTxAccessLists)
                    {
                        state.WarmUp(transaction.AccessList); // eip-2930
                    }

                    if (spec.UseHotAndColdStorage)
                    {
                        state.WarmUp(caller); // eip-2929
                        state.WarmUp(recipient); // eip-2929
                    }

                    substate = _virtualMachine.Run(state, txTracer);
                    unspentGas = state.GasAvailable;

                    if (txTracer.IsTracingAccess)
                    {
                        txTracer.ReportAccess(state.AccessedAddresses, state.AccessedStorageCells);
                    }
                }

                if (substate.ShouldRevert || substate.IsError)
                {
                    if (_logger.IsTrace) _logger.Trace("Restoring state from before transaction");
                    _stateProvider.Restore(stateSnapshot);
                    _storageProvider.Restore(storageSnapshot);
                }
                else
                {
                    // tks: there is similar code fo contract creation from init and from CREATE
                    // this may lead to inconsistencies (however it is tested extensively in blockchain tests)
                    if (transaction.IsContractCreation)
                    {
                        long codeDepositGasCost = CodeDepositHandler.CalculateCost(substate.Output.Length, spec);
                        if (unspentGas < codeDepositGasCost && spec.ChargeForTopLevelCreate)
                        {
                            throw new OutOfGasException();
                        }

                        if (unspentGas >= codeDepositGasCost)
                        {
                            Keccak codeHash = _stateProvider.UpdateCode(substate.Output);
                            _stateProvider.UpdateCodeHash(recipient, codeHash, spec);
                            unspentGas -= codeDepositGasCost;
                        }
                    }

                    foreach (Address toBeDestroyed in substate.DestroyList)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Destroying account {toBeDestroyed}");
                        _storageProvider.ClearStorage(toBeDestroyed);
                        _stateProvider.DeleteAccount(toBeDestroyed);
                        if (txTracer.IsTracingRefunds) txTracer.ReportRefund(RefundOf.Destroy);
                    }

                    statusCode = StatusCode.Success;
                }

                spentGas = Refund(gasLimit, unspentGas, substate, caller, gasPrice, spec);
            }
            catch (Exception ex) when (ex is EvmException || ex is OverflowException) // TODO: OverflowException? still needed? hope not
            {
                if (_logger.IsTrace) _logger.Trace($"EVM EXCEPTION: {ex.GetType().Name}");
                _stateProvider.Restore(stateSnapshot);
                _storageProvider.Restore(storageSnapshot);
            }

            if (_logger.IsTrace) _logger.Trace("Gas spent: " + spentGas);

            Address gasBeneficiary = block.GasBeneficiary;
            if (statusCode == StatusCode.Failure || !(substate?.DestroyList.Contains(gasBeneficiary) ?? false))
            {
                if (notSystemTransaction)
                {
                    if (!_stateProvider.AccountExists(gasBeneficiary))
                    {
                        _stateProvider.CreateAccount(gasBeneficiary, (ulong) spentGas * premiumPerGas);
                    }
                    else
                    {
                        _stateProvider.AddToBalance(gasBeneficiary, (ulong) spentGas * premiumPerGas, spec);
                    }
                }
            }

            if (!isCall)
            {
                _storageProvider.Commit(txTracer.IsTracingState ? txTracer : NullStorageTracer.Instance);
                _stateProvider.Commit(spec, txTracer.IsTracingState ? txTracer : NullStateTracer.Instance);
            }
            else
            {
                _storageProvider.Reset();
                _stateProvider.Reset();
                
                if (wasSenderAccountCreatedInsideACall)
                {
                    _stateProvider.DeleteAccount(caller);
                }
                else
                {
                    _stateProvider.AddToBalance(caller, senderReservedGasPayment, spec);
                    if (notSystemTransaction)
                    {
                        _stateProvider.DecrementNonce(caller);
                    }
                }
                
                _stateProvider.Commit(spec);
            }

            if (!isCall && notSystemTransaction)
            {
                block.GasUsed += spentGas;
            }

            if (txTracer.IsTracingReceipt)
            {
                Keccak stateRoot = null;
                bool eip658Enabled = _specProvider.GetSpec(block.Number).IsEip658Enabled;
                if (!eip658Enabled)
                {
                    _stateProvider.RecalculateStateRoot();
                    stateRoot = _stateProvider.StateRoot;
                }

                if (statusCode == StatusCode.Failure)
                {
                    txTracer.MarkAsFailed(recipientOrNull, spentGas, (substate?.ShouldRevert ?? false) ? substate.Output.ToArray() : Array.Empty<byte>(), substate?.Error, stateRoot);
                }
                else
                {
                    txTracer.MarkAsSuccess(recipientOrNull, spentGas, substate.Output.ToArray(), substate.Logs.Any() ? substate.Logs.ToArray() : Array.Empty<LogEntry>(), stateRoot);
                }
            }
        }

        private void TraceLogInvalidTx(Transaction transaction, string reason)
        {
            if (_logger.IsTrace) _logger.Trace($"Invalid tx {transaction.Hash} ({reason})");
        }
        
        private long Refund(long gasLimit, long unspentGas, TransactionSubstate substate, Address sender, UInt256 gasPrice, IReleaseSpec spec)
        {
            long spentGas = gasLimit;
            if (!substate.IsError)
            {
                spentGas -= unspentGas;
                long refund = substate.ShouldRevert ? 0 : RefundHelper.CalculateClaimableRefund(spentGas, substate.Refund + substate.DestroyList.Count * RefundOf.Destroy, spec);

                if (_logger.IsTrace) _logger.Trace("Refunding unused gas of " + unspentGas + " and refund of " + refund);
                _stateProvider.AddToBalance(sender, (ulong) (unspentGas + refund) * gasPrice, spec);
                spentGas -= refund;
            }

            return spentGas;
        }
    }
}
