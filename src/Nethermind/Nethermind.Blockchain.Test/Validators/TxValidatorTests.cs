﻿//  Copyright (c) 2021 Demerzel Solutions Limited
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

using FluentAssertions;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators
{
    [TestFixture]
    public class TxValidatorTests
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void Zero_r_is_not_valid()
        {
            byte[] sigData = new byte[65];
            // r is zero
            sigData[63] = 1; // correct s

            Signature signature = new Signature(sigData);
            var tx = Build.A.Transaction.WithSignature(signature).TestObject;
            
            TxValidator txValidator = new TxValidator(1);
            txValidator.IsWellFormed(tx, MuirGlacier.Instance).Should().BeFalse();
        }
        
        [Test]
        public void Zero_s_is_not_valid()
        {
            byte[] sigData = new byte[65];
            sigData[31] = 1; // correct r
            // s is zero
            
            Signature signature = new Signature(sigData);
            var tx = Build.A.Transaction.WithSignature(signature).TestObject;
            
            TxValidator txValidator = new TxValidator(1);
            txValidator.IsWellFormed(tx, MuirGlacier.Instance).Should().BeFalse();
        }
        
        [Test]
        public void Bad_chain_id_is_not_valid()
        {
            byte[] sigData = new byte[65];
            sigData[31] = 1; // correct r
            sigData[63] = 1; // correct s
            sigData[64] = 39;
            Signature signature = new Signature(sigData);
            var tx = Build.A.Transaction.WithSignature(signature).TestObject;
            
            TxValidator txValidator = new TxValidator(1);
            txValidator.IsWellFormed(tx, MuirGlacier.Instance).Should().BeFalse();
        }
        
        [Test]
        public void No_chain_id_tx_is_valid()
        {
            byte[] sigData = new byte[65];
            sigData[31] = 1; // correct r
            sigData[63] = 1; // correct s
            Signature signature = new Signature(sigData);
            var tx = Build.A.Transaction.WithSignature(signature).TestObject;
            
            TxValidator txValidator = new TxValidator(1);
            txValidator.IsWellFormed(tx, MuirGlacier.Instance).Should().BeTrue();
        }
        
        [Test]
        public void Is_valid_with_valid_chain_id()
        {
            byte[] sigData = new byte[65];
            sigData[31] = 1; // correct r
            sigData[63] = 1; // correct s
            sigData[64] = 38;
            Signature signature = new Signature(sigData);
            var tx = Build.A.Transaction.WithSignature(signature).TestObject;
            
            TxValidator txValidator = new TxValidator(1);
            txValidator.IsWellFormed(tx, MuirGlacier.Instance).Should().BeTrue();
        }
        
        [TestCase(true)]
        [TestCase(false)]
        public void Before_eip_155_has_to_have_valid_chain_id_unless_overridden(bool validateChainId)
        {
            byte[] sigData = new byte[65];
            sigData[31] = 1; // correct r
            sigData[63] = 1; // correct s
            sigData[64] = 41;
            Signature signature = new Signature(sigData);
            var tx = Build.A.Transaction.WithSignature(signature).TestObject;

            IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
            releaseSpec.IsEip155Enabled.Returns(false);
            releaseSpec.ValidateChainId.Returns(validateChainId);
            
            TxValidator txValidator = new TxValidator(1);
            txValidator.IsWellFormed(tx, releaseSpec).Should().Be(!validateChainId);
        }
        
        [TestCase(TxType.Legacy, true, ExpectedResult = true)]
        [TestCase(TxType.Legacy, false, ExpectedResult = true)]
        [TestCase(TxType.AccessList, false, ExpectedResult = false)]
        [TestCase(TxType.AccessList, true, ExpectedResult = true)]
        [TestCase((TxType)100, true, ExpectedResult = false)]
        public bool Before_eip_2930_has_to_be_legacy_tx(TxType txType, bool eip2930)
        {
            byte[] sigData = new byte[65];
            sigData[31] = 1; // correct r
            sigData[63] = 1; // correct s
            sigData[64] = 38;
            Signature signature = new Signature(sigData);
            var tx = Build.A.Transaction
                .WithType(txType > TxType.AccessList ? TxType.Legacy : txType)
                .WithChainId(ChainId.Mainnet)
                .WithSignature(signature).TestObject;

            tx.Type = txType;
            
            TxValidator txValidator = new TxValidator(1);
            return txValidator.IsWellFormed(tx, eip2930 ? Berlin.Instance : MuirGlacier.Instance);
        }
    }
}
