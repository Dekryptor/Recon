/*
 * Test corpus: a small C program that exercises the things the analyser has to get right.
 * Built by tools/build-corpus.sh with MinGW and the GNU linker's map output.
 */
#include <stdio.h>
#include <string.h>

int g_counter = 0;
int g_table[8] = { 1, 2, 3, 4, 5, 6, 7, 8 };
const char *g_message = "corpus";
static int s_internal = 7;

int __attribute__((noinline)) add(int a, int b)
{
    return a + b;
}

int __attribute__((noinline)) __attribute__((stdcall)) mul_std(int a, int b)
{
    return a * b;
}

int __attribute__((noinline)) __attribute__((fastcall)) sub_fast(int a, int b)
{
    return a - b;
}

/* Dense enough to produce a jump table at -O2. */
int __attribute__((noinline)) dispatch(int op)
{
    switch (op) {
    case 0: return add(1, 1);
    case 1: return add(2, 2);
    case 2: return add(3, 3);
    case 3: return add(4, 4);
    case 4: return add(5, 5);
    case 5: return add(6, 6);
    case 6: return add(7, 7);
    case 7: return g_table[7];
    case 8: return g_table[0] + s_internal;
    case 9: return mul_std(op, 3);
    case 10: return sub_fast(op, 1);
    default: return -1;
    }
}

int __attribute__((noinline)) fib(int n)
{
    if (n < 2) {
        return n;
    }

    return fib(n - 1) + fib(n - 2);
}

static void __attribute__((noinline)) bump(void)
{
    g_counter++;
}

void __attribute__((noinline)) loop_sum(int n)
{
    int sum = 0;
    for (int i = 0; i < n; i++) {
        sum += i * g_table[i & 7];
        bump();
    }

    g_counter = sum;
}

void __attribute__((noinline)) fail(const char *msg)
{
    fprintf(stderr, "%s: %s\n", g_message, msg);
}

/* Two functions with identical bodies: the analysis should notice, not merge them. */
static int __attribute__((noinline)) twin_a(int x)
{
    return (x * 3) + 1;
}

static int __attribute__((noinline)) twin_b(int x)
{
    return (x * 3) + 1;
}

int __attribute__((noinline)) use_twins(int x)
{
    return twin_a(x) + twin_b(x);
}

int main(int argc, char **argv)
{
    (void)argv;
    int total = 0;
    total += add(argc, 2);
    total += mul_std(argc, 3);
    total += sub_fast(argc, 1);
    total += dispatch(argc);
    total += fib(10);
    total += use_twins(argc);
    loop_sum(16);
    if (total == 12345) {
        fail("impossible");
    }

    printf("%d %d %s\n", total, g_counter, g_message);
    return total & 0x7f;
}
