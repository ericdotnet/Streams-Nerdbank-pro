﻿//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//     Runtime Version:4.0.30319.42000
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

namespace Nerdbank.Streams {
    using System;
    
    
    /// <summary>
    ///   A strongly-typed resource class, for looking up localized strings, etc.
    /// </summary>
    // This class was auto-generated by the StronglyTypedResourceBuilder
    // class via a tool like ResGen or Visual Studio.
    // To add or remove a member, edit your .ResX file then rerun ResGen
    // with the /str option, or rebuild your VS project.
    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("System.Resources.Tools.StronglyTypedResourceBuilder", "16.0.0.0")]
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
    [global::System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
    internal class Strings {
        
        private static global::System.Resources.ResourceManager resourceMan;
        
        private static global::System.Globalization.CultureInfo resourceCulture;
        
        [global::System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
        internal Strings() {
        }
        
        /// <summary>
        ///   Returns the cached ResourceManager instance used by this class.
        /// </summary>
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
        internal static global::System.Resources.ResourceManager ResourceManager {
            get {
                if (object.ReferenceEquals(resourceMan, null)) {
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Nerdbank.Streams.Strings", typeof(Strings).Assembly);
                    resourceMan = temp;
                }
                return resourceMan;
            }
        }
        
        /// <summary>
        ///   Overrides the current thread's CurrentUICulture property for all
        ///   resource lookups using this strongly typed resource class.
        /// </summary>
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
        internal static global::System.Globalization.CultureInfo Culture {
            get {
                return resourceCulture;
            }
            set {
                resourceCulture = value;
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to ExistingPipe&apos;s PipeWriter created with PipeOptions.PauseWriterThreshold that does not exceed the channel&apos;s receiving window size..
        /// </summary>
        internal static string ExistingPipeOutputHasPauseThresholdSetTooLow {
            get {
                return ResourceManager.GetString("ExistingPipeOutputHasPauseThresholdSetTooLow", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to This instance has been frozen. No mutations are allowed..
        /// </summary>
        internal static string Frozen {
            get {
                return ResourceManager.GetString("Frozen", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to This operation is not supported when the Channel is created with ChannelOptions.ExistingPipe set..
        /// </summary>
        internal static string NotSupportedWhenExistingPipeSpecified {
            get {
                return ResourceManager.GetString("NotSupportedWhenExistingPipeSpecified", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to No reading operation to complete..
        /// </summary>
        internal static string ReadBeforeAdvanceTo {
            get {
                return ResourceManager.GetString("ReadBeforeAdvanceTo", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to Reading not allowed after reader is completed..
        /// </summary>
        internal static string ReadingAfterCompletionNotAllowed {
            get {
                return ResourceManager.GetString("ReadingAfterCompletionNotAllowed", resourceCulture);
            }
        }
        
        /// <summary>
        ///   Looks up a localized string similar to Reading has already begun. Call AdvanceTo before reading again..
        /// </summary>
        internal static string ReadingMustBeFollowedByAdvance {
            get {
                return ResourceManager.GetString("ReadingMustBeFollowedByAdvance", resourceCulture);
            }
        }
    }
}
