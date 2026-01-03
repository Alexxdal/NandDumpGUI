#ifdef __cplusplus
extern "C" {
#endif

#if defined(_MSC_VER) && !defined(__clang__)

#ifndef __builtin_constant_p
#define __builtin_constant_p(x) 0
#endif

#ifndef __builtin_expect
#define __builtin_expect(x, expected) (x)
#endif

#ifndef __builtin_prefetch
#define __builtin_prefetch(addr, rw, locality) ((void)0)
#endif

#ifdef __cplusplus
}
#endif

#endif