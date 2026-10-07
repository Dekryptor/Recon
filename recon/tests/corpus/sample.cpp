/*
 * C++ corpus: virtual dispatch, constructors/destructors, a template, and some library code,
 * all mangled with the Itanium scheme because GCC built it.
 */
#include <stdio.h>
#include <string>
#include <vector>

class Shape
{
public:
    Shape() : sides_(0) {}
    virtual ~Shape() {}
    virtual int Sides() const { return sides_; }
    virtual double Area() const { return 0.0; }

protected:
    int sides_;
};

class Triangle : public Shape
{
public:
    Triangle(double base, double height) : base_(base), height_(height) { sides_ = 3; }
    double Area() const override { return 0.5 * base_ * height_; }

private:
    double base_;
    double height_;
};

class Square : public Shape
{
public:
    explicit Square(double side) : side_(side) { sides_ = 4; }
    double Area() const override { return side_ * side_; }

private:
    double side_;
};

template <typename T>
T Clamp(T value, T low, T high)
{
    if (value < low) {
        return low;
    }

    return value > high ? high : value;
}

double TotalArea(const std::vector<Shape *> &shapes)
{
    double total = 0.0;
    for (std::vector<Shape *>::const_iterator it = shapes.begin(); it != shapes.end(); ++it) {
        total += (*it)->Area();
    }

    return total;
}

int CountSides(const std::vector<Shape *> &shapes)
{
    int sides = 0;
    for (size_t i = 0; i < shapes.size(); i++) {
        sides += shapes[i]->Sides();
    }

    return sides;
}

int main()
{
    Triangle triangle(3.0, 4.0);
    Square square(2.5);
    std::vector<Shape *> shapes;
    shapes.push_back(&triangle);
    shapes.push_back(&square);

    int clamped = Clamp<int>(17, 0, 10);
    std::string label = "area";
    printf("%s %.3f sides %d clamped %d\n", label.c_str(), TotalArea(shapes), CountSides(shapes), clamped);
    return clamped;
}
