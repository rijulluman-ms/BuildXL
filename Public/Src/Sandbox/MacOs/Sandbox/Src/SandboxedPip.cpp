// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#include "SandboxedPip.hpp"

#define super OSObject

OSDefineMetaClassAndStructors(SandboxedPip, OSObject)

bool SandboxedPip::init(pid_t clientPid, pid_t processPid, Buffer *payload)
{
    if (!super::init())
    {
        return false;
    }

    clientPid_        = clientPid;
    payload_          = payload;
    processId_        = processPid;
    processTreeCount_ = 1;
    counters_         = {0};
    disableCaching_   = !g_bxl_enable_cache;
    cacheCallCnt_     = 0;

    payload_->retain();

    fam_.init((BYTE*)payload_->getBytes(), payload_->getSize());
    if (fam_.HasErrors())
    {
        log_error("Could not parse FileAccessManifest: %s", fam_.Error());
        return false;
    }

    oldPathCache_ = nullptr;
    pathCache_    = Trie::createPathTrie();
    if (!pathCache_)
    {
        return false;
    }
    
    lastPathLookup_ = ThreadLocal::create();
    if (!lastPathLookup_)
    {
        return false;
    }
    
    return true;
}

void SandboxedPip::free()
{
    if (pathCache_ != nullptr && lastPathLookup_ != nullptr)
    {
        log_verbose(
            g_bxl_verbose_logging,
           "Process Stats PID(%d) :: #cache hits = %d, #cache misses = %d, cache size = %d, thread local size = %d",
            processId_, counters_.numCacheHits.count(), counters_.numCacheMisses.count(),
            pathCache_->getCount(), lastPathLookup_->getCount());
    }

    OSSafeReleaseNULL(payload_);
    OSSafeReleaseNULL(lastPathLookup_);
    OSSafeReleaseNULL(pathCache_);
    OSSafeReleaseNULL(oldPathCache_);
    super::free();
}

SandboxedPip* SandboxedPip::create(pid_t clientPid, pid_t processPid, Buffer *payload)
{
    SandboxedPip *instance = new SandboxedPip;
    if (instance == nullptr)
    {
        log_error("Failed to create a new ProcessObject (PID: %d) for Client (PID: %d)", processPid, clientPid);
        return nullptr;
    }
    
    bool initialized = instance->init(clientPid, processPid, payload);
    if (!initialized)
    {
        // init already logged an error message describing what failed
        OSSafeReleaseNULL(instance);
        return nullptr;
    }
    
    return instance;
}

PipInfo SandboxedPip::introspect()
{
    return
    {
        .pid                 = getProcessId(),
        .clientPid           = getClientPid(),
        .pipId               = getPipId(),
        .cacheSize           = getPathCacheElemCount(),
        .treeSize            = getTreeSize(),
        .counters            = counters_,
        .numReportedChildren = 0,
        .children            = {0}
    };
}

bool SandboxedPip::RefreshDisableCaching()
{
    if (!disableCaching_)
    {
        if (ShouldDisableCaching())
        {
            // once caching is disabled, it must stay disabled
            disableCaching_ = true;
            Trie *oldCache = pathCache_;
            Trie *newCache = Trie::createPathTrie();
            if (OSCompareAndSwapPtr(oldCache, newCache, &pathCache_))
            {
                // we swapped --> save oldCache for garbage collection
                // (releasing is immediately is dangerous because it might still be in use in a concurrent thread)
                oldPathCache_ = oldCache;
            }
            else
            {
                // someone else did it --> release newCache that was created for nothing
                OSSafeReleaseNULL(newCache);
            }
        }
    }

    return disableCaching_;
}

# define PCT(a, b) (int)(((a) * 1.0) / ((a) + (b)) * 100)

inline bool SandboxedPip::ShouldDisableCaching()
{
    return
        // above the min_entries threshold
        pathCache_->getCount() > g_bxl_disable_cache_min_entries &&
        // below the max_hit_pct threshold
        PCT(counters_.numCacheHits, counters_.numCacheMisses) < g_bxl_disable_cache_max_hit_pct;
}

#undef super
