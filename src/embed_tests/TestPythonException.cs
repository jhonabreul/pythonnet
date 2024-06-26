using System;
using System.IO;
using System.Linq;

using NUnit.Framework;
using Python.Runtime;

namespace Python.EmbeddingTest
{
    public class TestPythonException
    {
        [OneTimeSetUp]
        public void SetUp()
        {
            PythonEngine.Initialize();

            // Add scripts folder to path in order to be able to import the test modules
            string testPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures");
            TestContext.Out.WriteLine(testPath);

            using var str = Runtime.Runtime.PyString_FromString(testPath);
            Assert.IsFalse(str.IsNull());
            BorrowedReference path = Runtime.Runtime.PySys_GetObject("path");
            Assert.IsFalse(path.IsNull);
            Runtime.Runtime.PyList_Append(path, str.Borrow());
        }

        [OneTimeTearDown]
        public void Dispose()
        {
            PythonEngine.Shutdown();
        }

        [Test]
        public void TestMessage()
        {
            var list = new PyList();
            PyObject foo = null;

            var ex = Assert.Throws<PythonException>(() => foo = list[0]);

            Assert.AreEqual("list index out of range", ex.Message);
            Assert.IsNull(foo);
        }

        [Test]
        public void TestType()
        {
            var list = new PyList();
            PyObject foo = null;

            var ex = Assert.Throws<PythonException>(() => foo = list[0]);

            Assert.AreEqual("IndexError", ex.Type.Name);
            Assert.IsNull(foo);
        }

        [Test]
        public void TestMessageComplete()
        {
            using (Py.GIL())
            {
                try
                {
                    // importing a module with syntax error 'x = 01' will throw
                    PyModule.FromString(Guid.NewGuid().ToString(), "x = 01");
                }
                catch (PythonException exception)
                {
                    Assert.True(exception.Message.Contains("x = 01"));
                    return;
                }
                Assert.Fail("No Exception was thrown!");
            }
        }

        [Test]
        public void TestNoError()
        {
            // There is no PyErr to fetch
            Assert.Throws<InvalidOperationException>(() => PythonException.FetchCurrentRaw());
            var currentError = PythonException.FetchCurrentOrNullRaw();
            Assert.IsNull(currentError);
        }

        [Test]
        public void TestNestedExceptions()
        {
            try
            {
                PythonEngine.Exec(@"
try:
  raise Exception('inner')
except Exception as ex:
  raise Exception('outer') from ex
");
            }
            catch (PythonException ex)
            {
                Assert.That(ex.InnerException, Is.InstanceOf<PythonException>());
                Assert.That(ex.InnerException.Message, Is.EqualTo("inner"));
            }
        }

        [Test]
        public void InnerIsEmptyWithNoCause()
        {
            var list = new PyList();
            PyObject foo = null;

            var ex = Assert.Throws<PythonException>(() => foo = list[0]);

            Assert.IsNull(ex.InnerException);
        }

        [Test]
        public void TestPythonExceptionFormat()
        {
            try
            {
                PythonEngine.Exec("raise ValueError('Error!')");
                Assert.Fail("Exception should have been raised");
            }
            catch (PythonException ex)
            {
                // Console.WriteLine($"Format: {ex.Format()}");
                // Console.WriteLine($"Stacktrace: {ex.StackTrace}");
                Assert.That(
                    ex.Format(),
                    Does.Contain("Traceback")
                    .And.Contains("(most recent call last):")
                    .And.Contains("ValueError: Error!")
                );

                // Check that the stacktrace is properly formatted
                Assert.That(
                    ex.StackTrace,
                    Does.Not.StartWith("[")
                    .And.Not.Contain("\\n")
                );
            }
        }

        [Test]
        public void TestPythonExceptionFormatNoTraceback()
        {
            try
            {
                var module = PyModule.Import("really____unknown___module");
                Assert.Fail("Unknown module should not be loaded");
            }
            catch (PythonException ex)
            {
                // ImportError/ModuleNotFoundError do not have a traceback when not running in a script
                Assert.AreEqual(ex.StackTrace, ex.Format());
            }
        }

        [Test]
        public void TestPythonExceptionFormatNormalized()
        {
            try
            {
                PythonEngine.Exec("a=b\n");
                Assert.Fail("Exception should have been raised");
            }
            catch (PythonException ex)
            {
                Assert.AreEqual("Traceback (most recent call last):\n  File \"<string>\", line 1, in <module>\nNameError: name 'b' is not defined\n", ex.Format());
            }
        }

        [Test]
        public void TestPythonException_PyErr_NormalizeException()
        {
            using (var scope = Py.CreateScope())
            {
                scope.Exec(@"
class TestException(NameError):
    def __init__(self, val):
        super().__init__(val)
        x = int(val)");
                Assert.IsTrue(scope.TryGet("TestException", out PyObject type));

                PyObject str = "dummy string".ToPython();
                var typePtr = new NewReference(type.Reference);
                var strPtr = new NewReference(str.Reference);
                var tbPtr = new NewReference(Runtime.Runtime.None.Reference);
                Runtime.Runtime.PyErr_NormalizeException(ref typePtr, ref strPtr, ref tbPtr);

                using var typeObj = typePtr.MoveToPyObject();
                using var strObj = strPtr.MoveToPyObject();
                using var tbObj = tbPtr.MoveToPyObject();
                // the type returned from PyErr_NormalizeException should not be the same type since a new
                // exception was raised by initializing the exception
                Assert.AreNotEqual(type.Handle, typeObj.Handle);
                // the message should now be the string from the throw exception during normalization
                Assert.AreEqual("invalid literal for int() with base 10: 'dummy string'", strObj.ToString());
            }
        }

        [Test]
        public void TestPythonException_Normalize_ThrowsWhenErrorSet()
        {
            Exceptions.SetError(Exceptions.TypeError, "Error!");
            var pythonException = PythonException.FetchCurrentRaw();
            Exceptions.SetError(Exceptions.TypeError, "Another error");
            Assert.Throws<InvalidOperationException>(() => pythonException.Normalize());
            Exceptions.Clear();
        }

        [Test]
        public void TestGetsPythonCodeInfoInStackTrace()
        {
            using (Py.GIL())
            {
                dynamic testClassModule = PyModule.FromString("TestGetsPythonCodeInfoInStackTrace_Module", @"
from clr import AddReference
AddReference(""Python.EmbeddingTest"")

from Python.EmbeddingTest import *

class TestPythonClass(TestPythonException.TestClass):
    def CallThrow(self):
        super().ThrowException()
");

                try
                {
                    var instance = testClassModule.TestPythonClass();
                    dynamic module = Py.Import("PyImportTest.SampleScript");
                    module.invokeMethod(instance, "CallThrow");
                }
                catch (ClrBubbledException ex)
                {
                    Assert.AreEqual("Test Exception Message", ex.InnerException.Message);

                    var pythonTracebackLines = ex.PythonTraceback.TrimEnd('\n').Split('\n').Select(x => x.Trim()).ToList();
                    Assert.AreEqual(5, pythonTracebackLines.Count);

                    Assert.AreEqual("File \"none\", line 9, in CallThrow", pythonTracebackLines[0]);

                    Assert.IsTrue(new[]
                    {
                        "File ",
                        "fixtures\\PyImportTest\\SampleScript.py",
                        "line 5",
                        "in invokeMethodImpl"
                    }.All(x => pythonTracebackLines[1].Contains(x)));
                    Assert.AreEqual("getattr(instance, method_name)()", pythonTracebackLines[2]);

                    Assert.IsTrue(new[]
                    {
                        "File ",
                        "fixtures\\PyImportTest\\SampleScript.py",
                        "line 2",
                        "in invokeMethod"
                    }.All(x => pythonTracebackLines[3].Contains(x)));
                    Assert.AreEqual("invokeMethodImpl(instance, method_name)", pythonTracebackLines[4]);
                }
                catch (Exception ex)
                {
                    Assert.Fail($"Unexpected exception: {ex}");
                }
            }
        }

        [Test]
        public void TestGetsPythonCodeInfoInStackTraceForNestedInterop()
        {
            using (Py.GIL())
            {
                dynamic testClassModule = PyModule.FromString("TestGetsPythonCodeInfoInStackTraceForNestedInterop_Module", @"
from clr import AddReference
AddReference(""Python.EmbeddingTest"")
AddReference(""System"")

from Python.EmbeddingTest import *
from System import Action

class TestPythonClass(TestPythonException.TestClass):
    def CallThrow(self):
        super().ThrowExceptionNested()

def GetThrowAction():
    return Action(CallThrow)

def CallThrow():
    TestPythonClass().CallThrow()
");

                try
                {
                    var action = testClassModule.GetThrowAction();
                    action();
                }
                catch (ClrBubbledException ex)
                {
                    Assert.AreEqual("Test Exception Message", ex.InnerException.Message);

                    var pythonTracebackLines = ex.PythonTraceback.TrimEnd('\n').Split('\n').Select(x => x.Trim()).ToList();
                    Assert.AreEqual(4, pythonTracebackLines.Count);

                    Assert.IsTrue(new[]
                    {
                        "File ",
                        "fixtures\\PyImportTest\\SampleScript.py",
                        "line 5",
                        "in invokeMethodImpl"
                    }.All(x => pythonTracebackLines[0].Contains(x)));
                    Assert.AreEqual("getattr(instance, method_name)()", pythonTracebackLines[1]);

                    Assert.IsTrue(new[]
                    {
                        "File ",
                        "fixtures\\PyImportTest\\SampleScript.py",
                        "line 2",
                        "in invokeMethod"
                    }.All(x => pythonTracebackLines[2].Contains(x)));
                    Assert.AreEqual("invokeMethodImpl(instance, method_name)", pythonTracebackLines[3]);
                }
                catch (Exception ex)
                {
                    Assert.Fail($"Unexpected exception: {ex}");
                }
            }
        }

        public class TestClass
        {
            public void ThrowException()
            {
                throw new ArgumentException("Test Exception Message");
            }

            public void ThrowExceptionNested()
            {
                using var _ = Py.GIL();

                dynamic module = Py.Import("PyImportTest.SampleScript");
                module.invokeMethod(this, "ThrowException");
            }
        }
    }
}
