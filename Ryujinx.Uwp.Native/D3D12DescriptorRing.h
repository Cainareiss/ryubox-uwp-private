#pragma once
#include <cstdint>
#include <deque>

// One contiguous table never crosses the physical end of a heap. Logical
// positions include wrap padding. Reservations carry the NEXT submission's
// fence, so even a list which has not been submitted cannot be overwritten.
// The command-recording owner serializes access to this allocator.
class D3D12DescriptorRing
{
    struct Retirement { uint64_t end, fence; };
    uint32_t capacity;
    uint64_t head = 0, tail = 0;
    std::deque<Retirement> retirements;
public:
    uint64_t wraps = 0, reclaimed = 0;
    explicit D3D12DescriptorRing(uint32_t count) : capacity(count) {}
    uint64_t Used() const { return head - tail; }
    uint64_t OldestFence() const { return retirements.empty() ? 0 : retirements.front().fence; }
    void Collect(uint64_t completed)
    {
        // UINT64_MAX means device removal, never GPU completion.
        if (completed == UINT64_MAX) return;
        while (!retirements.empty() && retirements.front().fence <= completed)
        {
            reclaimed += retirements.front().end - tail;
            tail = retirements.front().end;
            retirements.pop_front();
        }
    }
    bool Allocate(uint32_t count, uint64_t fence, uint32_t& offset)
    {
        if (!capacity || count > capacity || !fence || fence == UINT64_MAX) return false;
        if (!count) { offset = 0; return true; }
        if (!retirements.empty() && fence < retirements.back().fence) return false;
        uint64_t begin = head;
        uint64_t physical = head % capacity;
        uint64_t padding = physical + count > capacity ? capacity - physical : 0;
        // An empty ring may skip padding without reserving it.
        uint64_t effectiveTail = head == tail ? tail + padding : tail;
        if (head > UINT64_MAX - padding - count || head + padding + count - effectiveTail > capacity)
            return false;
        begin += padding;
        uint64_t end = begin + count;
        // Allocate retirement metadata before mutating cursors (bad_alloc).
        if (!retirements.empty() && retirements.back().fence == fence) retirements.back().end = end;
        else retirements.push_back({end, fence});
        head = end; tail = effectiveTail;
        if (padding || (begin != 0 && physical == 0)) ++wraps;
        offset = static_cast<uint32_t>(begin % capacity);
        return true;
    }
};
