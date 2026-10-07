/*
 * The unit that provides main, so the link step of `recon build` has somewhere to start. The
 * original's main is in tests/corpus/sample.c; this stub is what a reconstruction looks like before
 * that function has been rewritten.
 */
int __attribute__((noinline)) main(int argc, char **argv)
{
    (void)argc;
    (void)argv;
    return 0;
}
