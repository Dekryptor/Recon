// The source the .pdata measurement in docs/m7-status.md ("What is next" #5) was taken from.
// Small enough to build in under a second, and it has both halves of what the measurement needs:
// several leaf functions (no unwind info, so no .pdata entry) and several with prologues.
#include <stdio.h>
#include <setjmp.h>
static jmp_buf buf;
static int depth;
static int fib(int n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); }
int fail(void) { depth++; if (depth > 3) longjmp(buf, 1); return fib(depth); }
int main(void) { if (setjmp(buf) == 0) { printf("%d\n", fail()); } return 0; }
