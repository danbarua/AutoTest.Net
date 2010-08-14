using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using AutoTest.Core.Configuration;
using System.Reflection;
using Castle.MicroKernel.Registration;
using System.IO;
using AutoTest.Core.Messaging;
using AutoTest.Core.FileSystem;

namespace AutoTest.WinForms
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            tryStartApplication();
        }

        private static void  tryStartApplication()
		{
			try
			{
			    if (!ConfigureApplication())
                    return;
        	    var overviewForm = BootStrapper.Services.Locate<IOverviewForm>();
			    notifyOnLoggingSetup();
        	    Application.Run(overviewForm.Form);
        	    BootStrapper.ShutDown();
			}
			catch (Exception exception)
			{
				logException(exception);
			}
		}

		private static void logException (Exception exception)
		{
			var file = Path.Combine(PathParsing.GetRootDirectory(), "panic.dump");
			using (var writer = new StreamWriter(file))
			{
				writeException(writer, exception);
			}
		}
		
		private static void writeException(StreamWriter writer, Exception exception)
		{
			writer.WriteLine(string.Format("Message: {0}", exception.Message));
			writer.WriteLine("Stack trace:");
			writer.WriteLine(exception.StackTrace);
			if (exception.InnerException != null)
			{
				writer.WriteLine("Inner exception");
				writer.WriteLine("");
				writeException(writer, exception.InnerException);
			}
		}
			                 
        private static bool ConfigureApplication()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bootstrapApplication();
            var watchDirectory = getWatchDirectory();
            if (watchDirectory ==  null)
                return false;
            BootStrapper.InitializeCache();
            return true;
        }

        private static string getWatchDirectory()
        {
            var directoryPicker = BootStrapper.Services.Locate<IWatchDirectoryPicker>();
            if (directoryPicker.ShowDialog() == DialogResult.Cancel)
                return null;
            return directoryPicker.DirectoryToWatch;
        }

        public static void bootstrapApplication()
        {
            BootStrapper.Configure();
            BootStrapper.Container
                .Register(Component.For<IOverviewForm>().ImplementedBy<FeedbackForm>())
                .Register(Component.For<IInformationForm>().ImplementedBy<InformationForm>())
                .Register(Component.For<IWatchDirectoryPicker>().ImplementedBy<WatchDirectoryPickerForm>());
        }
		
		private static void notifyOnLoggingSetup()
		{
			var bus = BootStrapper.Services.Locate<IMessageBus>();
			bus.Publish<InformationMessage>(new InformationMessage("Debugging enabled"));
		}
    }
}
