/*
 * A reconstruction of three functions of sample-elf64-release. The point of the example is that the
 * toolchain is not named anywhere: `recon build` asks the binary who built it, finds GCC 14 for
 * x86-64 ELF, and runs that compiler with that profile's flags.
 *
 * On x86-64 the System V ABI is the only one, so the stdcall and fastcall attributes of the original
 * source mean nothing here and are left out.
 */
int __attribute__((noinline)) add(int a, int b)
{
    return a + b;
}

int __attribute__((noinline)) sub_fast(int a, int b)
{
    return a - b;
}

int __attribute__((noinline)) mul_std(int a, int b)
{
    return a * b;
}
