/*
 * What tests/corpus/sample.c uses from <stdio.h>, and nothing more.
 *
 * There is no macOS SDK on the machine that builds the Mach-O corpus, so the declarations the
 * source needs are said here instead. The corpus is read, never run, so the functions themselves
 * are never called: the linker is told to leave them for the dynamic linker
 * (-undefined dynamic_lookup), which is what the real build does with libSystem.
 */
#ifndef CORPUS_STDIO_H
#define CORPUS_STDIO_H

typedef struct __corpus_file FILE;

extern FILE *stdout;
extern FILE *stderr;

int printf(const char *format, ...);
int fprintf(FILE *stream, const char *format, ...);
int puts(const char *s);

#endif
