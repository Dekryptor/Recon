/* Freestanding x86 sample: no CRT, so it can be linked with lld-link alone. */
int g_counter;
int g_table[16];
int g_message_id = 7;

int __cdecl add(int a, int b) { return a + b; }
int __stdcall mul_std(int a, int b) { return a * b; }
int __fastcall sub_fast(int a, int b) { return a - b; }

int __cdecl dispatch(int op)
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
    case 8: return g_table[0] + g_message_id;
    case 9: return mul_std(op, 3);
    case 10: return sub_fast(op, 1);
    default: return -1;
    }
}

int __cdecl fib(int n)
{
    if (n < 2) return n;
    return fib(n - 1) + fib(n - 2);
}

static int __cdecl bump(void) { g_counter++; return g_counter; }

int __cdecl loop_sum(int n)
{
    int total = 0;
    for (int i = 0; i < n; i++) total += g_table[i & 15];
    return total + bump();
}

void __cdecl mainCRTStartup(void)
{
    int total = dispatch(9) + fib(10) + loop_sum(16);
    if (total == 12345) { g_counter = -1; }
    for (;;) { }
}
