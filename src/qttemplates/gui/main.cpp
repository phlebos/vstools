#include "$include$"
#include <QtWidgets/QApplication>

int main(int argc, char *argv[])
{
    QApplication a(argc, argv);
    $classname$ w;
    w.show();
    return a.exec();
}
