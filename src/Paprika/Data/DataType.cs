﻿namespace Paprika.Data;

/// <summary>
/// Represents the type of data stored in the <see cref="NibbleBasedMap"/>.
/// </summary>
public enum DataType : byte
{
    /// <summary>
    /// [key, 0]-> account (balance, nonce)
    /// </summary>
    Account = 0,

    /// <summary>
    /// [key, 1]-> codeHash
    /// </summary>
    CodeHash = 1,

    /// <summary>
    /// [key, 2]-> storageRootHash
    /// </summary>
    StorageRootHash = 2,

    /// <summary>
    /// [key, 3]-> [index][value],
    /// add to the key and use first 32 bytes of data as key
    /// </summary>
    StorageCell = 3,

    /// <summary>
    /// [key, 4]-> the DbAddress of the root page of the storage trie,
    /// </summary>
    StorageTreeRootPageAddress = 4,

    /// <summary>
    /// [storageCellIndex, 5]-> the StorageCell index, without the prefix of the account
    /// </summary>
    StorageTreeStorageCell = 5,

    /// <summary>
    /// As enums cannot be partial, this is for storing the Merkle.
    /// </summary>
    Merkle = 6,

    Deleted = 7,
    // one bit more is possible as delete is now a data type
}