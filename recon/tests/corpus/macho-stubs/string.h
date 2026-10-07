/*
 * tests/corpus/sample.c includes <string.h> but uses nothing from it on this target; the header
 * still has to exist for the compile to succeed. See stdio.h for why.
 */
#ifndef CORPUS_STRING_H
#define CORPUS_STRING_H

#endif
