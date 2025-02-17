﻿using System.Runtime.InteropServices;

namespace Paprika.Store.PageManagers;

public unsafe class NativeMemoryPageManager : PointerPageManager
{
    private readonly void* _ptr;

    public NativeMemoryPageManager(ulong size) : base(size)
    {
        _ptr = NativeMemory.AlignedAlloc((UIntPtr)size, (UIntPtr)Page.PageSize);
        NativeMemory.Clear(_ptr, (UIntPtr)size);
    }

    protected override void* Ptr => _ptr;

    public override void Flush() { }

    public override void Dispose() => NativeMemory.AlignedFree(_ptr);

    public override ValueTask FlushPages(ICollection<DbAddress> addresses, CommitOptions options) => ValueTask.CompletedTask;

    public override ValueTask FlushRootPage(DbAddress rootPage, CommitOptions options) => ValueTask.CompletedTask;
}