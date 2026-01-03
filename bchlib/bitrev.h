/* SPDX-License-Identifier: GPL-2.0 */
#ifndef _LINUX_BITREV_H
#define _LINUX_BITREV_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdint.h>
#include "compact_gcc_builtins.h"

	static __forceinline uint8_t __bitrev8(uint8_t x)
	{
		x = (uint8_t)((x >> 4) | (x << 4));
		x = (uint8_t)(((x & 0xCCu) >> 2) | ((x & 0x33u) << 2));
		x = (uint8_t)(((x & 0xAAu) >> 1) | ((x & 0x55u) << 1));
		return x;
	}

	static inline uint16_t __bitrev16(uint16_t x)
	{
		return (__bitrev8(x & 0xff) << 8) | __bitrev8(x >> 8);
	}

	static inline uint32_t __bitrev32(uint32_t x)
	{
		return (__bitrev16(x & 0xffff) << 16) | __bitrev16(x >> 16);
	}

#define __bitrev8x4(x)	(__bitrev32(swab32(x)))

#define __constant_bitrev32(x)	\
({					\
	uint32_t ___x = x;			\
	___x = (___x >> 16) | (___x << 16);	\
	___x = ((___x & (uint32_t)0xFF00FF00UL) >> 8) | ((___x & (uint32_t)0x00FF00FFUL) << 8);	\
	___x = ((___x & (uint32_t)0xF0F0F0F0UL) >> 4) | ((___x & (uint32_t)0x0F0F0F0FUL) << 4);	\
	___x = ((___x & (uint32_t)0xCCCCCCCCUL) >> 2) | ((___x & (uint32_t)0x33333333UL) << 2);	\
	___x = ((___x & (uint32_t)0xAAAAAAAAUL) >> 1) | ((___x & (uint32_t)0x55555555UL) << 1);	\
	___x;								\
})

#define __constant_bitrev16(x)	\
({					\
	uint16_t ___x = x;			\
	___x = (___x >> 8) | (___x << 8);	\
	___x = ((___x & (uint16_t)0xF0F0U) >> 4) | ((___x & (uint16_t)0x0F0FU) << 4);	\
	___x = ((___x & (uint16_t)0xCCCCU) >> 2) | ((___x & (uint16_t)0x3333U) << 2);	\
	___x = ((___x & (uint16_t)0xAAAAU) >> 1) | ((___x & (uint16_t)0x5555U) << 1);	\
	___x;								\
})

#define __constant_bitrev8x4(x) \
({			\
	uint32_t ___x = x;	\
	___x = ((___x & (uint32_t)0xF0F0F0F0UL) >> 4) | ((___x & (uint32_t)0x0F0F0F0FUL) << 4);	\
	___x = ((___x & (uint32_t)0xCCCCCCCCUL) >> 2) | ((___x & (uint32_t)0x33333333UL) << 2);	\
	___x = ((___x & (uint32_t)0xAAAAAAAAUL) >> 1) | ((___x & (uint32_t)0x55555555UL) << 1);	\
	___x;								\
})

#define __constant_bitrev8(x)	\
({					\
	uint8_t ___x = x;			\
	___x = (___x >> 4) | (___x << 4);	\
	___x = ((___x & (uint8_t)0xCCU) >> 2) | ((___x & (uint8_t)0x33U) << 2);	\
	___x = ((___x & (uint8_t)0xAAU) >> 1) | ((___x & (uint8_t)0x55U) << 1);	\
	___x;								\
})

	static __forceinline uint8_t  bitrev8(uint8_t  x) { return __bitrev8(x); }
	static __forceinline uint16_t bitrev16(uint16_t x) { return __bitrev16(x); }
	static __forceinline uint32_t bitrev32(uint32_t x) { return __bitrev32(x); }

#include <intrin.h>
	static __forceinline uint32_t bitrev8x4(uint32_t x)
	{
		/* equivalente a __bitrev32(swab32(x)) */
		return __bitrev32(_byteswap_ulong(x));
	}

	static __forceinline int fls_u32(uint32_t x)
	{
		unsigned long idx;
		if (x == 0) return 0;
		_BitScanReverse(&idx, x);     // idx = 0..31
		return (int)idx + 1;          // fls = 1..32
	}

#define fls(x) fls_u32((uint32_t)(x))

#define swap(a,b) do { \
    unsigned char _tmp[sizeof(a)]; \
    memcpy(_tmp, &(a), sizeof(a)); \
    memcpy(&(a), &(b), sizeof(a)); \
    memcpy(&(b), _tmp, sizeof(a)); \
} while(0)

#define ARRAY_SIZE(a) (sizeof(a) / sizeof((a)[0]))

#ifdef __cplusplus
}
#endif

#endif /* _LINUX_BITREV_H */