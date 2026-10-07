/* Freestanding C++ sample for the MSVC ABI: virtual functions and vtables, RTTI records, static and
 * const members, __stdcall/__fastcall free functions. No CRT, so it links with lld-link alone and
 * the whole path stays reproducible on Linux.
 *
 * This is the binary that exercises the MSVC name decoder end to end: the PDB stores C++ symbols in
 * their decorated form (?area@Circle@@UAENXZ), so the names the inventory shows come from decoding
 * them, and the vtables and RTTI records are data that must never appear as functions.
 */

extern "C" void mainCRTStartup(void);

int g_counter;
int g_table[16];
int g_message_id = 7;

/* ------------------------------------------------------------------ free functions */

int __cdecl add(int a, int b) { return a + b; }
int __stdcall mul_std(int a, int b) { return a * b; }
int __fastcall sub_fast(int a, int b) { return a - b; }

/* A switch wide enough for the compiler to build a jump table. */
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

int __cdecl loop_sum(int n)
{
    int total = 0;
    for (int i = 0; i < n; ++i) total += i * i - (i & 3);
    return total;
}

/* ------------------------------------------------------------------ classes */

class Shape
{
public:
    Shape(int id) : m_id(id) { ++g_counter; }
    virtual ~Shape() { --g_counter; }
    virtual int area() const { return 0; }
    virtual int perimeter() const = 0; /* pure: the vtable slot holds _purecall */

    int id() const { return m_id; }
    static int table_slot(int index) { return g_table[index & 15]; }

private:
    int m_id;
};

class Circle : public Shape
{
public:
    Circle(int radius) : Shape(1), m_radius(radius) {}
    int area() const override { return 3 * m_radius * m_radius; }
    int perimeter() const override { return 6 * m_radius; }

private:
    int m_radius;
};

class Square : public Shape
{
public:
    Square(int side) : Shape(2), m_side(side) {}
    int area() const override { return m_side * m_side; }
    int perimeter() const override { return 4 * m_side; }

private:
    int m_side;
};

/* A virtual call through a base pointer: the call site is an indirect call through the vtable. */
int __cdecl measure(Shape *shape)
{
    return shape->area() + shape->perimeter();
}

int __cdecl measure_both(int radius, int side)
{
    Circle circle(radius);
    Square square(side);
    return measure(&circle) + measure(&square) + circle.id() + square.id();
}

/* ------------------------------------------------------------------ entry point */

extern "C" void mainCRTStartup(void)
{
    g_table[0] = measure_both(3, 4);
    g_table[1] = dispatch(5) + fib(6) + loop_sum(7);
}
