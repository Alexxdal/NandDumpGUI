#pragma once
#include <stdint.h>

#if defined(_MSC_VER)  // MSVC

#include <intrin.h>

static __forceinline uint16_t cpu_to_be16(uint16_t x) { return _byteswap_ushort(x); }
static __forceinline uint32_t cpu_to_be32(uint32_t x) { return _byteswap_ulong(x); }
static __forceinline uint64_t cpu_to_be64(uint64_t x) { return _byteswap_uint64(x); }

static __forceinline uint16_t be16_to_cpu(uint16_t x) { return _byteswap_ushort(x); }
static __forceinline uint32_t be32_to_cpu(uint32_t x) { return _byteswap_ulong(x); }
static __forceinline uint64_t be64_to_cpu(uint64_t x) { return _byteswap_uint64(x); }

static __forceinline uint16_t cpu_to_le16(uint16_t x) { return x; }
static __forceinline uint32_t cpu_to_le32(uint32_t x) { return x; }
static __forceinline uint64_t cpu_to_le64(uint64_t x) { return x; }

static __forceinline uint16_t le16_to_cpu(uint16_t x) { return x; }
static __forceinline uint32_t le32_to_cpu(uint32_t x) { return x; }
static __forceinline uint64_t le64_to_cpu(uint64_t x) { return x; }

#endif