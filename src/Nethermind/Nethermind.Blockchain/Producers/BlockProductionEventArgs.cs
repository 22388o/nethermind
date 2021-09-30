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
// 

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Producers
{
    public class BlockProductionEventArgs : EventArgs
    {
        public static readonly Task<Block?> DefaultBlockProductionTask = Task.FromResult<Block?>(null);
        public BlockHeader? ParentHeader { get; }
        public IBlockTracer? BlockTracer { get; }
        public Address? BlockAuthor { get; }
        
        public UInt256? Timestamp { get; }
        public CancellationToken CancellationToken { get; }
        public Task<Block?> BlockProductionTask { get; set; } = DefaultBlockProductionTask;

        public BlockProductionEventArgs(
            BlockHeader? parentHeader = null, 
            CancellationToken? cancellationToken = null,
            IBlockTracer? blockTracer = null,
            Address? blockAuthor = null,
            UInt256? timestamp = null)
        {
            ParentHeader = parentHeader;
            BlockTracer = blockTracer;
            BlockAuthor = blockAuthor;
            Timestamp = timestamp;
            CancellationToken = cancellationToken ?? CancellationToken.None;
        }

        public BlockProductionEventArgs Clone() => (BlockProductionEventArgs)MemberwiseClone();
    }
}
