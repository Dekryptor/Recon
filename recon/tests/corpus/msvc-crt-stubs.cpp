/* The few symbols the Microsoft C++ runtime normally supplies, so that a freestanding C++ binary
 * can be linked with lld-link and /nodefaultlib. They exist to make the corpus linkable without a
 * CRT, not to do anything: nothing in the sample calls operator new, throws an exception or runs a
 * pure virtual call.
 *
 * Symbols are written the way the compiler mangles them:
 *   void operator delete(void *, unsigned int)   ??3@YAXPAXI@Z   (called by a deleting destructor)
 *   type_info's vftable                          ??_7type_info@@6B@  (referenced by every RTTI
 *                                                                     type descriptor)
 *   _purecall                                    __purecall       (the slot of a pure virtual
 *                                                                     function in a vtable)
 */

void __cdecl operator delete(void *pointer, unsigned int size) noexcept
{
    (void)pointer;
    (void)size;
}

void __cdecl operator delete(void *pointer) noexcept
{
    (void)pointer;
}

class type_info
{
public:
    virtual ~type_info();
};

type_info::~type_info()
{
}

/* RTTI descriptors point at type_info's vtable, so it has to exist even though nothing calls it.
 * Constructing an instance is what makes the compiler emit it. */
void touch_type_info(void)
{
    type_info instance;
    (void)instance;
}

extern "C" void _purecall(void)
{
}
