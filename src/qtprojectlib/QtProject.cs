/****************************************************************************
**
** Copyright (C) 2016 The Qt Company Ltd.
** Contact: https://www.qt.io/licensing/
**
** This file is part of the Qt VS Tools.
**
** $QT_BEGIN_LICENSE:GPL-EXCEPT$
** Commercial License Usage
** Licensees holding valid commercial Qt licenses may use this file in
** accordance with the commercial license agreement provided with the
** Software or, alternatively, in accordance with the terms contained in
** a written agreement between you and The Qt Company. For licensing terms
** and conditions see https://www.qt.io/terms-conditions. For further
** information use the contact form at https://www.qt.io/contact-us.
**
** GNU General Public License Usage
** Alternatively, this file may be used under the terms of the GNU
** General Public License version 3 as published by the Free Software
** Foundation with exceptions as appearing in the file LICENSE.GPL3-EXCEPT
** included in the packaging of this file. Please review the following
** information to ensure the GNU General Public License requirements will
** be met: https://www.gnu.org/licenses/gpl-3.0.html.
**
** $QT_END_LICENSE$
**
****************************************************************************/

using EnvDTE;
using Microsoft.VisualStudio.VCProjectEngine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using QtProjectLib.QtMsBuild;

namespace QtProjectLib
{
    /// <summary>
    /// QtProject holds the Qt specific properties for a Visual Studio project.
    /// There exists at most one QtProject per EnvDTE.Project.
    /// Use QtProject.Create to get the QtProject for a Project or VCProject.
    /// </summary>
    public class QtProject
    {
        private DTE dte;
        private Project envPro;
        private VCProject vcPro;
        private MocCmdChecker mocCmdChecker;
        private Array lastConfigurationRowNames;
        private static Dictionary<Project, QtProject> instances = new Dictionary<Project, QtProject>();
        private QtMsBuildContainer qtMsBuild;

        public static QtProject Create(VCProject vcProject)
        {
            return Create((Project) vcProject.Object);
        }

        public static QtProject Create(Project project)
        {
            QtProject qtProject = null;
            if (project != null && !instances.TryGetValue(project, out qtProject)) {
                qtProject = new QtProject(project);
                instances.Add(project, qtProject);
            }
            return qtProject;
        }

        public static void ClearInstances()
        {
            instances.Clear();
        }

        private QtProject(Project project)
        {
            if (project == null)
                throw new QtVSException(SR.GetString("QtProject_CannotConstructWithoutValidProject"));
            envPro = project;
            dte = envPro.DTE;
            vcPro = envPro.Object as VCProject;
            qtMsBuild = new QtMsBuildContainer(new VCPropertyStorageProvider());
        }

        public VCProject VCProject
        {
            get { return vcPro; }
        }

        public Project Project
        {
            get { return envPro; }
        }

        public static bool IsQtMsBuildEnabled(VCProject project)
        {
            if (project == null)
                return false;
            try {
                var configs = project.Configurations as IVCCollection;
                if (configs.Count == 0)
                    return false;
                var firstConfig = configs.Item(1) as VCConfiguration;
                var qtMoc = firstConfig.Rules.Item(QtMoc.ItemTypeName) as IVCRulePropertyStorage;
                if (qtMoc == null)
                    return false;
            } catch (Exception) {
                return false;
            }
            return true;
        }

        public static bool IsQtMsBuildEnabled(Project project)
        {
            if (project == null)
                return false;
            return IsQtMsBuildEnabled(project.Object as VCProject);
        }

        private bool? isQtMsBuildEnabled = null;
        public bool IsQtMsBuildEnabled()
        {
            if (!isQtMsBuildEnabled.HasValue) {
                if (vcPro != null)
                    isQtMsBuildEnabled = IsQtMsBuildEnabled(vcPro);
                else if (envPro != null)
                    isQtMsBuildEnabled = IsQtMsBuildEnabled(envPro);
                else
                    return false;
            }
            return isQtMsBuildEnabled.Value;
        }

        public string ProjectDir
        {
            get
            {
                return vcPro.ProjectDirectory;
            }
        }

        /// <summary>
        /// Returns true if the ConfigurationRowNames have changed
        /// since the last evaluation of this property.
        /// </summary>
        public bool ConfigurationRowNamesChanged
        {
            get
            {
                var ret = false;
                if (lastConfigurationRowNames == null) {
                    lastConfigurationRowNames = envPro.ConfigurationManager.ConfigurationRowNames as Array;
                } else {
                    var currentConfigurationRowNames = envPro.ConfigurationManager.ConfigurationRowNames as Array;
                    if (!HelperFunctions.ArraysEqual(lastConfigurationRowNames, currentConfigurationRowNames)) {
                        lastConfigurationRowNames = currentConfigurationRowNames;
                        ret = true;
                    }
                }
                return ret;
            }
        }

        /// <summary>
        /// Returns the file name of the generated ui header file relative to
        /// the project directory.
        /// </summary>
        /// <param name="uiFile">name of the ui file</param>
        public string GetUiGeneratedFileName(string uiFile)
        {
            var fi = new FileInfo(uiFile);
            var file = fi.Name;
            if (HelperFunctions.IsUicFile(file)) {
                return QtVSIPSettings.GetUicDirectory(envPro)
                    + "\\ui_" + file.Remove(file.Length - 3, 3) + ".h";
            }
            return null;
        }

        /// <summary>
        /// Returns the moc-generated file name for the given source or header file.
        /// </summary>
        /// <param name="file">header or source file in the project</param>
        /// <returns></returns>
        private static string GetMocFileName(string file)
        {
            var fi = new FileInfo(file);

            var name = fi.Name;
            if (HelperFunctions.IsHeaderFile(fi.Name))
                return "moc_" + name.Substring(0, name.LastIndexOf('.')) + ".cpp";
            if (HelperFunctions.IsSourceFile(fi.Name))
                return name.Substring(0, name.LastIndexOf('.')) + ".moc";
            return null;
        }

        /// <summary>
        /// Returns the file name of the generated moc file relative to the
        /// project directory.
        /// </summary>
        /// The directory of the moc file depends on the file configuration.
        /// Every appearance of "$(ConfigurationName)" in the path will be
        /// replaced by the value of configName.
        /// <param name="file">full file name of either the header or the source file</param>
        /// <returns></returns>
        private string GetRelativeMocFilePath(string file, string configName, string platformName)
        {
            var fileName = GetMocFileName(file);
            if (fileName == null)
                return null;
            var mocDir = QtVSIPSettings.GetMocDirectory(envPro, configName, platformName, file)
                + "\\" + fileName;
            if (HelperFunctions.IsAbsoluteFilePath(mocDir))
                mocDir = HelperFunctions.GetRelativePath(vcPro.ProjectDirectory, mocDir);
            return mocDir;
        }

        /// <summary>
        /// Returns the file name of the generated moc file relative to the
        /// project directory.
        /// </summary>
        /// The returned file path may contain the macros $(ConfigurationName) and $(PlatformName).
        /// <param name="file">full file name of either the header or the source file</param>
        /// <returns></returns>
        private string GetRelativeMocFilePath(string file)
        {
            return GetRelativeMocFilePath(file, null, null);
        }

        /// <summary>
        /// Marks the specified project as a Qt project.
        /// </summary>
        public void MarkAsQtProject(string version)
        {
            vcPro.keyword = Resources.qtProjectKeyword + version;
        }

        public void AddDefine(string define, uint bldConf)
        {
            foreach (VCConfiguration config in (IVCCollection) vcPro.Configurations) {
                var compiler = CompilerToolWrapper.Create(config);

                if (((!IsDebugConfiguration(config)) && ((bldConf & BuildConfig.Release) != 0)) ||
                    ((IsDebugConfiguration(config)) && ((bldConf & BuildConfig.Debug) != 0))) {
                    compiler.AddPreprocessorDefinition(define);
                }
            }
        }

        public void AddModule(QtModule module)
        {
            if (HasModule(module))
                return;

            var vm = QtVersionManager.The();
            var versionInfo = vm.GetVersionInfo(Project);
            if (versionInfo == null)
                versionInfo = vm.GetVersionInfo(vm.GetDefaultVersion());

            foreach (VCConfiguration config in (IVCCollection) vcPro.Configurations) {
                var compiler = CompilerToolWrapper.Create(config);
                var linker = (VCLinkerTool) ((IVCCollection) config.Tools).Item("VCLinkerTool");

                var info = QtModules.Instance.ModuleInformation(module);
                if (compiler != null) {
                    foreach (var define in info.Defines)
                        compiler.AddPreprocessorDefinition(define);

                    var incPathList = info.GetIncludePath();
                    foreach (var incPath in incPathList)
                        compiler.AddAdditionalIncludeDirectories(incPath);
                }
                if (linker != null) {
                    var moduleLibs = info.GetLibs(IsDebugConfiguration(config), versionInfo);
                    var linkerWrapper = new LinkerToolWrapper(linker);
                    var additionalDeps = linkerWrapper.AdditionalDependencies;
                    var dependenciesChanged = false;
                    if (additionalDeps == null || additionalDeps.Count == 0) {
                        additionalDeps = moduleLibs;
                        dependenciesChanged = true;
                    } else {
                        foreach (var moduleLib in moduleLibs) {
                            if (!additionalDeps.Contains(moduleLib)) {
                                additionalDeps.Add(moduleLib);
                                dependenciesChanged = true;
                            }
                        }
                    }
                    if (dependenciesChanged)
                        linkerWrapper.AdditionalDependencies = additionalDeps;
                }
            }
        }

        public void RemoveModule(QtModule module)
        {
            foreach (VCConfiguration config in (IVCCollection) vcPro.Configurations) {
                var compiler = CompilerToolWrapper.Create(config);
                var linker = (VCLinkerTool) ((IVCCollection) config.Tools).Item("VCLinkerTool");

                var info = QtModules.Instance.ModuleInformation(module);
                if (compiler != null) {
                    foreach (var define in info.Defines)
                        compiler.RemovePreprocessorDefinition(define);
                    var additionalIncludeDirs = compiler.AdditionalIncludeDirectories;
                    if (additionalIncludeDirs != null) {
                        var lst = new List<string>(additionalIncludeDirs);
                        foreach (var includePath in info.IncludePath) {
                            lst.Remove(includePath);
                            lst.Remove('\"' + includePath + '\"');
                        }
                        compiler.AdditionalIncludeDirectories = lst;
                    }
                }
                if (linker != null && linker.AdditionalDependencies != null) {
                    var linkerWrapper = new LinkerToolWrapper(linker);
                    var vm = QtVersionManager.The();
                    var versionInfo = vm.GetVersionInfo(Project);
                    if (versionInfo == null)
                        versionInfo = vm.GetVersionInfo(vm.GetDefaultVersion());

                    var moduleLibs = info.GetLibs(IsDebugConfiguration(config), versionInfo);
                    var additionalDependencies = linkerWrapper.AdditionalDependencies;
                    var dependenciesChanged = false;
                    foreach (var moduleLib in moduleLibs)
                        dependenciesChanged |= additionalDependencies.Remove(moduleLib);
                    if (dependenciesChanged)
                        linkerWrapper.AdditionalDependencies = additionalDependencies;
                }
            }
        }

        public void UpdateModules(VersionInformation oldVersion, VersionInformation newVersion)
        {
            foreach (VCConfiguration config in (IVCCollection) vcPro.Configurations) {
                var linker = (VCLinkerTool) ((IVCCollection) config.Tools).Item("VCLinkerTool");

                if (linker != null) {
                    if (oldVersion == null) {
                        var linkerWrapper = new LinkerToolWrapper(linker);
                        var additionalDependencies = linkerWrapper.AdditionalDependencies;

                        var libsDesktop = new List<string>();
                        foreach (var module in QtModules.Instance.GetAvailableModuleInformation()) {
                            if (HasModule(module.ModuleId))
                                libsDesktop.AddRange(module.AdditionalLibraries);
                        }
                        var libsToAdd = libsDesktop;

                        var changed = false;
                        foreach (var libToAdd in libsToAdd) {
                            if (!additionalDependencies.Contains(libToAdd)) {
                                additionalDependencies.Add(libToAdd);
                                changed = true;
                            }
                        }
                        if (changed)
                            linkerWrapper.AdditionalDependencies = additionalDependencies;
                    }

                    if (newVersion.qtMajor >= 5) {
                        var compiler = CompilerToolWrapper.Create(config);
                        if (compiler != null)
                            compiler.RemovePreprocessorDefinition("QT_DLL");
                        continue;
                    }

                    if (oldVersion == null || newVersion.IsStaticBuild() != oldVersion.IsStaticBuild()) {
                        var compiler = CompilerToolWrapper.Create(config);
                        if (newVersion.IsStaticBuild()) {
                            if (compiler != null)
                                compiler.RemovePreprocessorDefinition("QT_DLL");
                        } else {
                            if (compiler != null)
                                compiler.AddPreprocessorDefinition("QT_DLL");
                        }
                    }
                }
            }
        }

        public bool HasModule(QtModule module)
        {
            var foundInIncludes = false;
            var foundInLibs = false;

            var vm = QtVersionManager.The();
            var versionInfo = vm.GetVersionInfo(Project);
            if (versionInfo == null)
                versionInfo = vm.GetVersionInfo(vm.GetDefaultVersion());
            if (versionInfo == null)
                return false; // neither a default or project Qt version
            var info = QtModules.Instance.ModuleInformation(module);
            if (info == null)
                return false;

            foreach (VCConfiguration config in (IVCCollection) vcPro.Configurations) {
                var compiler = CompilerToolWrapper.Create(config);
                var linker = (VCLinkerTool) ((IVCCollection) config.Tools).Item("VCLinkerTool");

                if (compiler != null) {
                    if (compiler.GetAdditionalIncludeDirectories() == null)
                        continue;
                    var incPathList = info.GetIncludePath();
                    var includeDirs = compiler.GetAdditionalIncludeDirectoriesList();
                    foundInIncludes = (incPathList.Count > 0);
                    foreach (var incPath in incPathList) {
                        var fixedIncludeDir = FixFilePathForComparison(incPath);
                        if (!includeDirs.Any(dir =>
                            FixFilePathForComparison(dir) == fixedIncludeDir)) {
                            foundInIncludes = false;
                            break;
                        }
                    }
                }

                if (foundInIncludes)
                    break;

                List<string> libs = null;
                if (linker != null) {
                    var linkerWrapper = new LinkerToolWrapper(linker);
                    libs = linkerWrapper.AdditionalDependencies;
                }

                if (libs != null) {
                    var moduleLibs = info.GetLibs(IsDebugConfiguration(config), versionInfo);
                    foundInLibs = moduleLibs.All(moduleLib => libs.Contains(moduleLib));
                }
            }
            return foundInIncludes || foundInLibs;
        }

        public void WriteProjectBasicConfigurations(uint type, bool usePrecompiledHeader)
        {
            WriteProjectBasicConfigurations(type, usePrecompiledHeader, null);
        }

        public void WriteProjectBasicConfigurations(uint type, bool usePrecompiledHeader, VersionInformation vi)
        {
            var configType = ConfigurationTypes.typeApplication;
            var targetExtension = ".exe";
            string qtVersion = null;
            var vm = QtVersionManager.The();
            if (vi == null) {
                qtVersion = vm.GetDefaultVersion();
                vi = vm.GetVersionInfo(qtVersion);
            }

            switch (type & TemplateType.ProjectType) {
            case TemplateType.DynamicLibrary:
                configType = ConfigurationTypes.typeDynamicLibrary;
                targetExtension = ".dll";
                break;
            case TemplateType.StaticLibrary:
                configType = ConfigurationTypes.typeStaticLibrary;
                targetExtension = ".lib";
                break;
            }

            foreach (VCConfiguration config in (IVCCollection) vcPro.Configurations) {
                config.ConfigurationType = configType;
                var compiler = CompilerToolWrapper.Create(config);
                var linker = (VCLinkerTool) ((IVCCollection) config.Tools).Item("VCLinkerTool");
                var librarian = (VCLibrarianTool) ((IVCCollection) config.Tools).Item("VCLibrarianTool");

                // for some stupid reason you have to set this for it to be updated...
                // the default value is the same... +platform now
                config.OutputDirectory = "$(SolutionDir)$(Platform)\\$(Configuration)\\";

                // add some common defines
                compiler.SetPreprocessorDefinitions(vi.GetQMakeConfEntry("DEFINES").Replace(" ", ","));

                if (!vi.IsStaticBuild())
                    compiler.AddPreprocessorDefinition("QT_DLL");

                if (linker != null) {
                    if ((type & TemplateType.ConsoleSystem) != 0)
                        linker.SubSystem = subSystemOption.subSystemConsole;
                    else
                        linker.SubSystem = subSystemOption.subSystemWindows;

                    linker.OutputFile = "$(OutDir)\\$(ProjectName)" + targetExtension;
                    linker.AdditionalLibraryDirectories = "$(QTDIR)\\lib";
                    if (vi.IsStaticBuild()) {
                        linker.AdditionalDependencies = vi.GetQMakeConfEntry("QMAKE_LIBS_CORE");
                        if ((type & TemplateType.GUISystem) != 0)
                            linker.AdditionalDependencies += " " + vi.GetQMakeConfEntry("QMAKE_LIBS_GUI");
                    }
                } else {
                    librarian.OutputFile = "$(OutDir)\\$(ProjectName)" + targetExtension;
                    librarian.AdditionalLibraryDirectories = "$(QTDIR)\\lib";
                }

                if ((type & TemplateType.GUISystem) != 0)
                    compiler.SetAdditionalIncludeDirectories(QtVSIPSettings.GetUicDirectory(envPro) + ";");

                if ((type & TemplateType.PluginProject) != 0)
                    compiler.AddPreprocessorDefinition("QT_PLUGIN");

                var isDebugConfiguration = false;
                if (config.Name.StartsWith("Release", StringComparison.Ordinal)) {
                    compiler.AddPreprocessorDefinition("QT_NO_DEBUG,NDEBUG");
                    compiler.SetDebugInformationFormat(debugOption.debugDisabled);
                    compiler.RuntimeLibrary = runtimeLibraryOption.rtMultiThreadedDLL;
                } else if (config.Name.StartsWith("Debug", StringComparison.Ordinal)) {
                    isDebugConfiguration = true;
                    compiler.SetOptimization(optimizeOption.optimizeDisabled);
                    compiler.SetDebugInformationFormat(debugOption.debugEnabled);
                    compiler.RuntimeLibrary = runtimeLibraryOption.rtMultiThreadedDebugDLL;
                }
                compiler.AddAdditionalIncludeDirectories(
                    ".;" + "$(QTDIR)\\include;" + QtVSIPSettings.GetMocDirectory(envPro));

                compiler.SetTreatWChar_tAsBuiltInType(true);

                if (linker != null)
                    linker.GenerateDebugInformation = isDebugConfiguration;

                if (usePrecompiledHeader)
                    UsePrecompiledHeaders(config);
            }
            if ((type & TemplateType.PluginProject) != 0)
                MarkAsDesignerPluginProject();
        }

        public void MarkAsDesignerPluginProject()
        {
            Project.Globals["IsDesignerPlugin"] = true.ToString();
            if (!Project.Globals.get_VariablePersists("IsDesignerPlugin"))
                Project.Globals.set_VariablePersists("IsDesignerPlugin", true);
        }

        public void AddUic4BuildStepMsBuild(
            VCFileConfiguration config,
            string description,
            string outputFile)
        {
            var file = config.File as VCFile;
            if (file != null)
                file.ItemType = QtUic.ItemTypeName;
            qtMsBuild.SetItemProperty(config, QtUic.Property.ExecutionDescription, description);
            qtMsBuild.SetItemProperty(config, QtUic.Property.OutputFile, outputFile);
        }

        public void AddUic4BuildStepCustomBuild(
            VCFileConfiguration config,
            string description,
            string outputFile)
        {
            //SetItemType(config, ItemType.CustomBuild);
            var tool = HelperFunctions.GetCustomBuildTool(config);
            if (tool != null) {
                tool.AdditionalDependencies = Resources.uic4Command;
                tool.Description = description;
                tool.Outputs = "\"" + outputFile + "\"";
                tool.CommandLine = "\"" + Resources.uic4Command + "\" -o \""
                    + outputFile + "\" \"" + ProjectMacros.Path + "\"";
            }
        }

        /// <summary>
        /// This function adds a uic4 build step to a given file.
        /// </summary>
        /// <param name="file">file</param>
        public void AddUic4BuildStep(VCFile file)
        {
            CustomTool toolSettings =
                IsQtMsBuildEnabled() ? CustomTool.MSBuildTarget : CustomTool.CustomBuildStep;

            try {
                var uiFile = GetUiGeneratedFileName(file.FullPath);
                var uiBaseName = file.Name.Remove(file.Name.LastIndexOf('.'));
                var uiFileMacro = uiFile.Replace(uiBaseName, ProjectMacros.Name);
                var uiFileExists = GetFileFromProject(uiFile) != null;
                string description = "Uic'ing " + ProjectMacros.FileName + "...";

                foreach (VCFileConfiguration config in (IVCCollection) file.FileConfigurations) {

                    switch (toolSettings) {
                        case CustomTool.MSBuildTarget:
                            AddUic4BuildStepMsBuild(config, description, uiFileMacro);
                            break;
                        default:
                            AddUic4BuildStepCustomBuild(config, description, uiFileMacro);
                            break;
                    }

                    var conf = config.ProjectConfiguration as VCConfiguration;
                    var compiler = CompilerToolWrapper.Create(conf);
                    if (compiler != null && !uiFileExists) {
                        var uiDir = QtVSIPSettings.GetUicDirectory(envPro);
                        if (compiler.GetAdditionalIncludeDirectories().IndexOf(uiDir, StringComparison.Ordinal) < 0)
                            compiler.AddAdditionalIncludeDirectories(uiDir);
                    }
                }
                if (toolSettings == CustomTool.CustomBuildStep && !uiFileExists)
                    AddFileInFilter(Filters.GeneratedFiles(), uiFile);
            } catch {
                throw new QtVSException(SR.GetString("QtProject_CannotAddUicStep", file.FullPath));
            }
        }

        /// <summary>
        /// Surrounds the argument by double quotes.
        /// Makes sure, that the trailing double quote is not escaped by a backslash.
        /// Such an escaping backslash may also appear as a macro value.
        /// </summary>
        private static string SafelyQuoteCommandLineArgument(string arg)
        {
            arg = "\"" + arg;
            if (arg.EndsWith("\\", StringComparison.Ordinal))
                arg += ".";     // make sure, that we don't escape the trailing double quote
            else if (arg.EndsWith(")", StringComparison.Ordinal))
                arg += "\\.";   // macro value could end with backslash. That would escape the trailing double quote.
            arg += "\"";
            return arg;
        }

        public string GetDefines(VCFileConfiguration conf)
        {
            var defines = string.Empty;
            var propsFile = conf.Tool as IVCRulePropertyStorage;
            var projectConfig = conf.ProjectConfiguration as VCConfiguration;
            var propsProject = projectConfig.Rules.Item("CL") as IVCRulePropertyStorage;
            if (propsFile != null) {
                try {
                    defines = propsFile.GetEvaluatedPropertyValue("PreprocessorDefinitions");
                } catch { }
            }
            if (string.IsNullOrEmpty(defines) && propsProject != null) {
                try {
                    defines = propsProject.GetEvaluatedPropertyValue("PreprocessorDefinitions");
                } catch { }
            }

            if (string.IsNullOrEmpty(defines))
                return string.Empty;

            var defineList = defines.Split(
                new char[] { ';' },
                StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            var preprocessorDefines = string.Empty;
            var alreadyAdded = new List<string>();
            var rxp = new Regex(@"\s|(\$\()");
            foreach (var define in defineList) {
                if (!alreadyAdded.Contains(define)) {
                    var mustSurroundByDoubleQuotes = rxp.IsMatch(define);
                    // Yes, a preprocessor definition can contain spaces or a macro name.
                    // Example: PROJECTDIR=$(InputDir)

                    if (mustSurroundByDoubleQuotes) {
                        preprocessorDefines += " ";
                        preprocessorDefines += SafelyQuoteCommandLineArgument("-D" + define);
                    } else {
                        preprocessorDefines += " -D" + define;
                    }
                    alreadyAdded.Add(define);
                }
            }
            return preprocessorDefines;
        }

        private List<string> GetDefinesFromPropertySheet(VCPropertySheet sheet)
        {
            var defines = CompilerToolWrapper.Create(sheet).PreprocessorDefinitions;
            var propertySheets = sheet.PropertySheets as IVCCollection;
            if (propertySheets != null) {
                foreach (VCPropertySheet subSheet in propertySheets)
                    defines.AddRange(GetDefinesFromPropertySheet(subSheet));
            }
            return defines;
        }

        private string GetIncludes(VCFileConfiguration conf)
        {
            var includeList = GetIncludesFromCompilerTool(CompilerToolWrapper.Create(conf));

            var projectConfig = conf.ProjectConfiguration as VCConfiguration;
            includeList.AddRange(GetIncludesFromCompilerTool(CompilerToolWrapper.Create(projectConfig)));

            var propertySheets = projectConfig.PropertySheets as IVCCollection;
            if (propertySheets != null) {
                foreach (VCPropertySheet sheet in propertySheets)
                    includeList.AddRange(GetIncludesFromPropertySheet(sheet));
            }

            var includes = string.Empty;
            var alreadyAdded = new List<string>();
            foreach (var include in includeList) {
                if (!alreadyAdded.Contains(include)) {
                    var incl = HelperFunctions.NormalizeRelativeFilePath(include);
                    if (incl.Length > 0)
                        includes += " " + SafelyQuoteCommandLineArgument("-I" + incl);
                    alreadyAdded.Add(include);
                }
            }
            return includes;
        }

        private List<string> GetIncludesFromPropertySheet(VCPropertySheet sheet)
        {
            var includeList = GetIncludesFromCompilerTool(CompilerToolWrapper.Create(sheet));
            var propertySheets = sheet.PropertySheets as IVCCollection;
            if (propertySheets != null) {
                foreach (VCPropertySheet subSheet in propertySheets)
                    includeList.AddRange(GetIncludesFromPropertySheet(subSheet));
            }
            return includeList;
        }

        private static List<string> GetIncludesFromCompilerTool(CompilerToolWrapper compiler)
        {
            try {
                if (!string.IsNullOrEmpty(compiler.GetAdditionalIncludeDirectories())) {
                    var includes = compiler.GetAdditionalIncludeDirectoriesList();
                    return new List<string>(includes);
                }
            } catch { }
            return new List<string>();
        }

        private static bool IsDebugConfiguration(VCConfiguration conf)
        {
            var tool = CompilerToolWrapper.Create(conf);
            if (tool != null) {
                return tool.RuntimeLibrary == runtimeLibraryOption.rtMultiThreadedDebug
                    || tool.RuntimeLibrary == runtimeLibraryOption.rtMultiThreadedDebugDLL;
            }
            return false;
        }

        private string GetPCHMocOptions(VCFile file, CompilerToolWrapper compiler)
        {
            // As .moc files are included, we should not add anything there
            if (!HelperFunctions.IsHeaderFile(file.Name))
                return string.Empty;

            var additionalMocOptions = "\"-f" + compiler.GetPrecompiledHeaderThrough().Replace('\\', '/') + "\" ";
            //Get mocDir without .\\ at the beginning of it
            var mocDir = QtVSIPSettings.GetMocDirectory(envPro);
            if (mocDir.StartsWith(".\\", StringComparison.Ordinal))
                mocDir = mocDir.Substring(2);

            //Get the absolute path
            mocDir = vcPro.ProjectDirectory + mocDir;
            var fullPathGeneric = Path.Combine(
                Path.GetDirectoryName(file.FullPath), "%(Filename)%(Extension)");
            var relPathToFile = HelperFunctions.GetRelativePath(
                mocDir, fullPathGeneric).Replace('\\', '/');
            additionalMocOptions += "\"-f" + relPathToFile + "\"";
            return additionalMocOptions;
        }


        void AddMocStepSetBuildExclusions(
            VCFile sourceFile,
            VCFileConfiguration workFileConfig,
            VCFile mocFile)
        {
            var hasDifferentMocFilePerConfig =
                QtVSIPSettings.HasDifferentMocFilePerConfig(envPro);
            var hasDifferentMocFilePerPlatform =
                QtVSIPSettings.HasDifferentMocFilePerPlatform(envPro);

            var workFile = workFileConfig.File as VCFile;
            var mocFileName = GetMocFileName(sourceFile.FullPath);
            var mocableIsCPP = HelperFunctions.IsMocFile(mocFileName);
            var vcConfig = workFileConfig.ProjectConfiguration as VCConfiguration;
            var platform = vcConfig.Platform as VCPlatform;
            var platformName = platform.Name;

            if (hasDifferentMocFilePerPlatform && hasDifferentMocFilePerConfig) {
                foreach (VCFileConfiguration mocConf
                    in (IVCCollection)mocFile.FileConfigurations) {
                    var projectCfg = mocConf.ProjectConfiguration as VCConfiguration;
                    if (projectCfg.Name != vcConfig.Name
                        || (IsMoccedFileIncluded(sourceFile) && !mocableIsCPP)) {
                        if (!mocConf.ExcludedFromBuild)
                            mocConf.ExcludedFromBuild = true;
                    } else {
                        if (mocConf.ExcludedFromBuild != workFileConfig.ExcludedFromBuild)
                            mocConf.ExcludedFromBuild = workFileConfig.ExcludedFromBuild;
                    }
                }
            } else if (hasDifferentMocFilePerPlatform) {
                foreach (VCFileConfiguration mocConf
                    in (IVCCollection)mocFile.FileConfigurations) {
                    var projectCfg = mocConf.ProjectConfiguration as VCConfiguration;
                    var mocConfPlatform = projectCfg.Platform as VCPlatform;
                    if (projectCfg.ConfigurationName != vcConfig.ConfigurationName)
                        continue;

                    var exclude = mocConfPlatform.Name != platformName
                        || (IsMoccedFileIncluded(sourceFile) && !mocableIsCPP);
                    if (exclude) {
                        if (mocConf.ExcludedFromBuild != exclude)
                            mocConf.ExcludedFromBuild = exclude;
                    } else {
                        if (mocConf.ExcludedFromBuild != workFileConfig.ExcludedFromBuild)
                            mocConf.ExcludedFromBuild = workFileConfig.ExcludedFromBuild;
                    }
                }
            } else if (hasDifferentMocFilePerConfig) {
                foreach (VCFileConfiguration mocConf
                    in (IVCCollection)mocFile.FileConfigurations) {
                    var projectCfg = mocConf.ProjectConfiguration as VCConfiguration;
                    var mocConfPlatform = projectCfg.Platform as VCPlatform;
                    if (platformName != mocConfPlatform.Name)
                        continue;
                    if (projectCfg.Name != vcConfig.Name
                        || (IsMoccedFileIncluded(sourceFile))) {
                        if (!mocConf.ExcludedFromBuild)
                            mocConf.ExcludedFromBuild = true;
                    } else {
                        if (mocConf.ExcludedFromBuild != workFileConfig.ExcludedFromBuild)
                            mocConf.ExcludedFromBuild = workFileConfig.ExcludedFromBuild;
                    }
                }
            } else {
                var moccedFileConfig = GetVCFileConfigurationByName(
                    mocFile,
                    workFileConfig.Name);
                if (moccedFileConfig != null) {
                    var cppFile = GetCppFileForMocStep(sourceFile);
                    if (cppFile != null && IsMoccedFileIncluded(cppFile)) {
                        if (!moccedFileConfig.ExcludedFromBuild)
                            moccedFileConfig.ExcludedFromBuild = true;
                    } else if (moccedFileConfig.ExcludedFromBuild
                        != workFileConfig.ExcludedFromBuild) {
                        moccedFileConfig.ExcludedFromBuild = workFileConfig.ExcludedFromBuild;
                    }
                }
            }
        }

        void AddMocStepCustomBuild(
            VCFile sourceFile,
            VCFileConfiguration workFileConfig,
            VCFile mocFile,
            string defines,
            string includes,
            string description)
        {
            var workFile = workFileConfig.File as VCFile;
            var mocFileName = GetMocFileName(sourceFile.FullPath);
            var mocableIsCPP = HelperFunctions.IsMocFile(mocFileName);
            var vcConfig = workFileConfig.ProjectConfiguration as VCConfiguration;

            workFile.ItemType = "CustomBuild";

            VCCustomBuildTool tool = HelperFunctions.GetCustomBuildTool(workFileConfig);
            string fileToMoc = null;
            if (mocableIsCPP) {
                fileToMoc = HelperFunctions.GetRelativePath(
                    vcPro.ProjectDirectory,
                    sourceFile.FullPath);
            } else {
                fileToMoc = ProjectMacros.Path;
            }
            if (tool == null)
                throw new QtVSException(
                    SR.GetString("QtProject_CannotFindCustomBuildTool",
                    workFile.FullPath));

            var dps = tool.AdditionalDependencies;
            if (dps.IndexOf("\"" + Resources.moc4Command + "\"", StringComparison.Ordinal) < 0) {
                if (dps.Length > 0 && !dps.EndsWith(";", StringComparison.Ordinal))
                    dps += ";";
                tool.AdditionalDependencies = dps + "\""
                    + Resources.moc4Command + "\";" + fileToMoc;
            }

            tool.Description = description;

            var baseFileName = sourceFile.Name.Remove(sourceFile.Name.LastIndexOf('.'));
            var outputMocFile = string.Empty;
            var outputMocMacro = string.Empty;

            var inputMocFile = ProjectMacros.Path;
            if (mocableIsCPP)
                inputMocFile = sourceFile.RelativePath;
            var output = tool.Outputs;
            var pattern = "(\"(.*\\\\" + mocFileName + ")\"|(\\S*"
                + mocFileName + "))";
            var regExp = new Regex(pattern);
            var matchList = regExp.Matches(tool.Outputs.Replace(ProjectMacros.Name, baseFileName));
            if (matchList.Count > 0) {
                if (matchList[0].Length > 0)
                    outputMocFile = matchList[0].ToString();
                else if (matchList[1].Length > 1)
                    outputMocFile = matchList[1].ToString();

                if (outputMocFile.StartsWith("\"", StringComparison.Ordinal))
                    outputMocFile = outputMocFile.Substring(1);
                if (outputMocFile.EndsWith("\"", StringComparison.Ordinal))
                    outputMocFile = outputMocFile.Substring(0, outputMocFile.Length - 1);
                var outputMocPath = Path.GetDirectoryName(outputMocFile);
                var stringToReplace = Path.GetFileName(outputMocFile);
                outputMocMacro =
                    outputMocPath
                    + "\\"
                    + stringToReplace.Replace(baseFileName, ProjectMacros.Name);
            } else {
                outputMocFile = GetRelativeMocFilePath(sourceFile.FullPath);
                var outputMocPath = Path.GetDirectoryName(outputMocFile);
                var stringToReplace = Path.GetFileName(outputMocFile);
                outputMocMacro =
                    outputMocPath
                    + "\\"
                    + stringToReplace.Replace(baseFileName, ProjectMacros.Name);
                if (output.Length > 0 && !output.EndsWith(";", StringComparison.Ordinal))
                    output += ";";
                tool.Outputs = output + "\"" + outputMocMacro + "\"";
            }

            var newCmdLine = "\"" + Resources.moc4Command + "\" "
                + QtVSIPSettings.GetMocOptions(envPro)
                + " \"" + inputMocFile + "\" -o \""
                + outputMocMacro + "\"";

            //Tell moc to include the PCH header if we are using precompiled headers in the project
            var compiler = CompilerToolWrapper.Create(vcConfig);
            if (compiler.GetUsePrecompiledHeader() != pchOption.pchNone)
                newCmdLine += " " + GetPCHMocOptions(sourceFile, compiler);

            var versionManager = QtVersionManager.The();
            var versionInfo = VersionInformation.Get(versionManager.GetInstallPath(envPro));
            var mocSupportsIncludes = (versionInfo.qtMajor == 4 && versionInfo.qtMinor >= 2)
                || versionInfo.qtMajor >= 5;

            var strDefinesIncludes = defines + includes;
            var cmdLineLength = newCmdLine.Length + strDefinesIncludes.Length + 1;

            if (cmdLineLength > HelperFunctions.GetMaximumCommandLineLength()
                && mocSupportsIncludes) {
                // Command line is too long. We must use an options file.
                var mocIncludeCommands = string.Empty;
                var mocIncludeFile = "\"" + outputMocFile + ".inc\"";
                var redirectOp = " > ";
                var maxCmdLineLength =
                    HelperFunctions.GetMaximumCommandLineLength()
                    - (mocIncludeFile.Length + 1);

                var options = defines.Split(
                    new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                var matches = Regex.Matches(includes, "([\"])(?:(?=(\\\\?))\\2.)*?\\1");
                foreach (Match match in matches) {
                    options.Add(match.Value);
                    includes = includes.Replace(
                        match.Value, string.Empty, StringComparison.Ordinal);
                }
                options.AddRange(
                    includes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

                // Since 5.2.0, MOC uses QCommandLineParser and parses the content of
                // the moc_*.inc file differently. For example, "-I.\foo\bar" results
                // in an error message, because the parser thinks it got an additional
                // positional argument. Change the option into a format MOC understands.
                if (versionInfo.qtMajor == 5 && versionInfo.qtMinor >= 2) {
                    for (var o = 0; o < options.Count; ++o)
                        options[o] = Regex.Replace(options[o], "\"(-I|-D)", "$1=\"");
                }

                var i = options.Count - 1;
                for (; i >= 0; --i) {
                    if (options[i].Length == 0)
                        continue;
                    mocIncludeCommands +=
                        "echo " + options[i] + redirectOp + mocIncludeFile + "\r\n";
                    cmdLineLength -= options[i].Length + 1;
                    if (cmdLineLength < maxCmdLineLength)
                        break;
                    if (i == options.Count - 1)    // first loop
                        redirectOp = " >> ";
                }
                strDefinesIncludes = "@" + mocIncludeFile;
                for (var k = 0; k < i; ++k) {
                    if (options[k].Length > 0)
                        strDefinesIncludes += " " + options[k];
                }
                newCmdLine = mocIncludeCommands + newCmdLine + " " + strDefinesIncludes;
            } else {
                newCmdLine = newCmdLine + " " + strDefinesIncludes;
            }

            if (tool.CommandLine.Trim().Length > 0) {
                var cmdLine = tool.CommandLine;

                // remove the moc option file commands
                {
                    var rex = new Regex("^echo.+[.](moc|cpp)[.]inc\"\r\n", RegexOptions.Multiline);
                    cmdLine = rex.Replace(cmdLine, string.Empty);
                }

                var m = Regex.Match(cmdLine, @"(\S*moc.exe|""\S+:\\\.*moc.exe"")");
                if (m.Success) {
                    var start = m.Index;
                    var end = cmdLine.IndexOf("&&", start, StringComparison.Ordinal);
                    var a = cmdLine.IndexOf("\r\n", start, StringComparison.Ordinal);
                    if (a > -1 && (a < end || end < 0))
                        end = a;
                    if (end < 0)
                        end = cmdLine.Length;
                    tool.CommandLine = cmdLine.Replace(
                        cmdLine.Substring(start, end - start), newCmdLine);
                } else {
                    tool.CommandLine = cmdLine + "\r\n" + newCmdLine;
                }
            } else {
                tool.CommandLine = newCmdLine;
            }
        }

        void AddMocStepMsBuildTarget(
            VCFile sourceFile,
            VCFileConfiguration workConfig,
            string defines,
            string includes,
            string description)
        {
            var baseFileName = sourceFile.Name.Remove(sourceFile.Name.LastIndexOf('.'));
            var outputMocFile = GetRelativeMocFilePath(sourceFile.FullPath);
            var outputMocPath = Path.GetDirectoryName(outputMocFile);
            var stringToReplace = Path.GetFileName(outputMocFile);
            var outputMocMacro = outputMocPath + "\\"
                + stringToReplace.Replace(baseFileName, ProjectMacros.Name);

            sourceFile.ItemType = QtMoc.ItemTypeName;
            qtMsBuild.SetItemProperty(workConfig,
                QtMoc.Property.InputFile, ProjectMacros.Path);
            qtMsBuild.SetItemProperty(workConfig,
                QtMoc.Property.OutputFile, outputMocMacro);
            if (!HelperFunctions.IsSourceFile(sourceFile.FullPath)) {
                qtMsBuild.SetItemProperty(workConfig,
                    QtMoc.Property.DynamicSource, "output");
            } else {
                qtMsBuild.SetItemProperty(workConfig,
                    QtMoc.Property.DynamicSource, "input");
            }
            qtMsBuild.SetItemProperty(workConfig,
                QtMoc.Property.ExecutionDescription, description);
            qtMsBuild.SetQtMocCommandLine(workConfig,
                QtMoc.ToolExecName + " " + defines + " " + includes,
                new VCMacroExpander(workConfig));
        }

        void AddMocStepToConfiguration(
            VCFile sourceFile,
            VCFileConfiguration workConfig,
            CustomTool toolSettings)
        {
            var workFile = workConfig.File as VCFile;
            var mocFileName = GetMocFileName(sourceFile.FullPath);
            var mocableIsCPP = HelperFunctions.IsMocFile(mocFileName);
            var vcConfig = workConfig.ProjectConfiguration as VCConfiguration;
            var platform = vcConfig.Platform as VCPlatform;
            var platformName = platform.Name;

            var mocRelPath = GetRelativeMocFilePath(
                sourceFile.FullPath,
                vcConfig.ConfigurationName,
                platformName);
            string subfilterName = null;
            if (mocRelPath.Contains(vcConfig.ConfigurationName))
                subfilterName = vcConfig.ConfigurationName;
            if (mocRelPath.Contains(platformName)) {
                if (subfilterName != null)
                    subfilterName += '_';
                subfilterName += platformName;
            }

            VCFile mocFile = null;
            if (toolSettings == CustomTool.CustomBuildStep) {
                mocFile = GetFileFromProject(mocRelPath);
                if (mocFile == null) {
                    var fi = new FileInfo(VCProject.ProjectDirectory + "\\" + mocRelPath);
                    if (!fi.Directory.Exists)
                        fi.Directory.Create();
                    mocFile = AddFileInSubfilter(Filters.GeneratedFiles(), subfilterName,
                        mocRelPath);
                }
                if (mocFile != null) {
                    if (mocableIsCPP)
                        mocFile.ItemType = "None";
                    else
                        AddMocStepSetBuildExclusions(sourceFile, workConfig, mocFile);
                }
            }

            VCFile cppPropertyFile = null;
            if (mocableIsCPP)
                cppPropertyFile = sourceFile;
            else if (mocFile != null)
                cppPropertyFile = GetCppFileForMocStep(sourceFile);
            VCFileConfiguration defineIncludeConfig;
            if (cppPropertyFile != null) {
                defineIncludeConfig = GetVCFileConfigurationByName(
                    cppPropertyFile,
                    workConfig.Name);
            } else {
                // No file specific defines/includes
                // but at least the project defines/includes are added
                defineIncludeConfig = workConfig;
            }
            var defines = GetDefines(defineIncludeConfig);
            var includes = GetIncludes(defineIncludeConfig);
            var description = "Moc'ing %(Identity)...";

            if (toolSettings == CustomTool.MSBuildTarget) {
                AddMocStepMsBuildTarget(
                    sourceFile,
                    workConfig,
                    defines,
                    includes,
                    description);
            } else {
                AddMocStepCustomBuild(
                    sourceFile,
                    workConfig,
                    mocFile,
                    defines,
                    includes,
                    description);
            }
        }

        public enum CustomTool { CustomBuildStep, MSBuildTarget };

        /// <summary>
        /// Adds a moc step to a given file for this project.
        /// </summary>
        /// <param name="file">file</param>
        public void AddMocStep(VCFile file)
        {
            CustomTool toolSettings =
                IsQtMsBuildEnabled() ? CustomTool.MSBuildTarget : CustomTool.CustomBuildStep;

            try {
                var mocFileName = GetMocFileName(file.FullPath);
                if (mocFileName == null)
                    return;

                var mocableIsCPP = HelperFunctions.IsMocFile(mocFileName);

                VCFile sourceFile = file;
                if (mocableIsCPP && toolSettings != CustomTool.MSBuildTarget) {
                    string cbtFullPath = Path.ChangeExtension(file.FullPath, ".cbt");
                    File.WriteAllText(cbtFullPath, string.Format(
                        "This is a dummy file needed to create {0}", mocFileName));
                    file = AddFileInSubfilter(Filters.GeneratedFiles(), null, cbtFullPath, true);
                    var mocFileItem = file.Object as ProjectItem;
                    if (mocFileItem != null)
                        HelperFunctions.EnsureCustomBuildToolAvailable(mocFileItem);
                }

                foreach (VCFileConfiguration config in (IVCCollection) file.FileConfigurations)
                    AddMocStepToConfiguration(sourceFile, config, toolSettings);

            } catch {
                throw new QtVSException(SR.GetString("QtProject_CannotAddMocStep", file.FullPath));
            }
        }

        /// <summary>
        /// Parses the given file to find an occurrence of a moc.exe generated file include. If
        /// the given file is a header file, the function tries to find the corresponding source
        /// file to use it instead of the header file. Helper function for AddMocStep.
        /// </summary>
        /// <param name="vcFile">Header or source file name.</param>
        /// <returns>
        /// Returns true if the file contains an include of the corresponding moc_xxx.cpp file;
        /// otherwise returns false.
        /// </returns>
        public bool IsMoccedFileIncluded(VCFile vcFile)
        {
            var fullPath = vcFile.FullPath;
            if (HelperFunctions.IsHeaderFile(fullPath))
                fullPath = Path.ChangeExtension(fullPath, ".cpp");

            if (HelperFunctions.IsSourceFile(fullPath)) {
                vcFile = GetFileFromProject(fullPath);
                if (vcFile == null)
                    return false;

                fullPath = vcFile.FullPath;
                var mocFile = "moc_" + Path.GetFileNameWithoutExtension(fullPath) + ".cpp";

#if TODO
                // TODO: Newly created projects need a manual solution rescan if we access the
                // code model too early, right now it fails to properly parse the created files.

                // Try reusing the vc file code model,
                var projectItem = vcFile.Object as ProjectItem;
                if (projectItem != null) {
                    var vcFileCodeModel = projectItem.FileCodeModel as VCFileCodeModel;
                    if (vcFileCodeModel != null) {
                        foreach (VCCodeInclude include in vcFileCodeModel.Includes) {
                            if (include.FullName == mocFile)
                                return true;
                        }
                        return false;
                    }
                }

                // if we fail, we parse the file on our own...
#endif
                CxxStreamReader cxxStream = null;
                try {
                    var line = string.Empty;
                    cxxStream = new CxxStreamReader(fullPath);
                    while ((line = cxxStream.ReadLine()) != null) {
                        if (Regex.IsMatch(line, "#include *(<|\")" + mocFile + "(\"|>)"))
                            return true;
                    }
                } catch { } finally {
                    if (cxxStream != null)
                        cxxStream.Dispose();
                }
            }
            return false;
        }

        public bool HasMocStep(VCFile file, string mocOutDir = null)
        {
            if (file.ItemType == QtMoc.ItemTypeName)
                return true;

            if (HelperFunctions.IsHeaderFile(file.Name))
                return CheckForCommand(file, "moc.exe");

            if (HelperFunctions.IsSourceFile(file.Name)) {
                return (HasCppMocFiles(file));
            }
            return false;
        }

        public static bool HasUicStep(VCFile file)
        {
            if (file.ItemType == QtUic.ItemTypeName)
                return true;
            return CheckForCommand(file, Resources.uic4Command);
        }

        private static bool CheckForCommand(VCFile file, string cmd)
        {
            if (file == null)
                return false;
            foreach (VCFileConfiguration config in (IVCCollection) file.FileConfigurations) {
                var tool = HelperFunctions.GetCustomBuildTool(config);
                if (tool == null)
                    return false;
                if (tool.CommandLine != null && tool.CommandLine.Contains(cmd))
                    return true;
            }
            return false;
        }

        public void RefreshRccSteps()
        {
            Messages.PaneMessage(dte, "\r\n=== Update rcc steps ===");
            var files = GetResourceFiles();

            var vcFilter = FindFilterFromGuid(Filters.GeneratedFiles().UniqueIdentifier);
            if (vcFilter != null) {
                var filterFiles = GetAllFilesFromFilter(vcFilter);
                var filesToDelete = new List<VCFile>();
                foreach (VCFile rmFile in filterFiles) {
                    if (rmFile.Name.StartsWith("qrc_", StringComparison.OrdinalIgnoreCase))
                        filesToDelete.Add(rmFile);
                }
                foreach (var rmFile in filesToDelete) {
                    RemoveFileFromFilter(rmFile, vcFilter);
                    HelperFunctions.DeleteEmptyParentDirs(rmFile);
                }
            }

            qtMsBuild.BeginSetItemProperties();
            foreach (var file in files) {
                Messages.PaneMessage(dte, "Update rcc step for " + file.Name + ".");
                var options = new RccOptions(envPro, file);
                UpdateRccStep(file, options);
            }
            qtMsBuild.EndSetItemProperties();

            Messages.PaneMessage(dte, "\r\n=== " + files.Count + " rcc steps updated. ===\r\n");
        }

        public void RefreshRccSteps(string oldRccDir)
        {
            RefreshRccSteps();
            UpdateCompilerIncludePaths(oldRccDir, QtVSIPSettings.GetRccDirectory(envPro));
        }

        public void UpdateRccStepMsBuild(
            VCFileConfiguration vfc,
            RccOptions rccOpts,
            string filesInQrcFile,
            string nameOnly,
            string qrcCppFile)
        {
            var file = vfc.File as VCFile;
            if (file != null)
                file.ItemType = QtRcc.ItemTypeName;
            qtMsBuild.SetItemProperty(vfc,
                QtRcc.Property.ExecutionDescription, "Rcc'ing " + ProjectMacros.FileName + "...");
            qtMsBuild.SetItemProperty(vfc,
                QtRcc.Property.OutputFile, qrcCppFile.Replace(nameOnly, ProjectMacros.Name));
        }

        public void UpdateRccStepCustomBuild(
            VCFileConfiguration vfc,
            RccOptions rccOpts,
            string filesInQrcFile,
            string nameOnly,
            string qrcCppFile)
        {
            var qrcFile = vfc.File as VCFile;
            var rccOptsCfg = rccOpts;
            var cmdLine = string.Empty;

            var cbt = HelperFunctions.GetCustomBuildTool(vfc);

            cbt.AdditionalDependencies = filesInQrcFile;

            cbt.Description = "Rcc'ing " + ProjectMacros.FileName + "...";

            cbt.Outputs = qrcCppFile.Replace(nameOnly, ProjectMacros.Name);

            cmdLine += "\"" + Resources.rcc4Command + "\""
                + " -name \"" + ProjectMacros.Name + "\"";

            if (rccOptsCfg == null)
                rccOptsCfg = HelperFunctions.ParseRccOptions(cbt.CommandLine, qrcFile);

            if (rccOptsCfg.CompressFiles) {
                cmdLine += " -threshold " + rccOptsCfg.CompressThreshold;
                cmdLine += " -compress " + rccOptsCfg.CompressLevel;
            } else {
                cmdLine += " -no-compress";
            }

            cbt.CommandLine = cmdLine + " \"" + ProjectMacros.Path + "\" -o " + cbt.Outputs;
        }

        public void UpdateRccStep(VCFile qrcFile, RccOptions rccOpts)
        {
            CustomTool toolSettings =
                IsQtMsBuildEnabled() ? CustomTool.MSBuildTarget : CustomTool.CustomBuildStep;

            var vcpro = (VCProject) qrcFile.project;
            var dteObject = ((Project) vcpro.Object).DTE;

            var qtPro = Create(vcpro);
            var parser = new QrcParser(qrcFile.FullPath);
            var filesInQrcFile = ProjectMacros.Path;

            if (parser.parse()) {
                var fi = new FileInfo(qrcFile.FullPath);
                var qrcDir = fi.Directory.FullName + "\\";

                foreach (var prfx in parser.Prefixes) {
                    foreach (var itm in prfx.Items) {
                        var relativeQrcItemPath = HelperFunctions.GetRelativePath(
                            vcPro.ProjectDirectory,
                            qrcDir + itm.Path);
                        filesInQrcFile += ";" + relativeQrcItemPath;
                        try {
                            var addedFile = qtPro.AddFileInFilter(
                                Filters.ResourceFiles(),
                                relativeQrcItemPath, true);
                            ExcludeFromAllBuilds(addedFile);
                        } catch { /* it's not possible to add all kinds of files */ }
                    }
                }
            }

            var nameOnly = HelperFunctions.RemoveFileNameExtension(new FileInfo(qrcFile.FullPath));
            var qrcCppFile = QtVSIPSettings.GetRccDirectory(envPro)
                + "\\" + "qrc_" + nameOnly + ".cpp";

            try {
                foreach (VCFileConfiguration vfc in (IVCCollection) qrcFile.FileConfigurations) {
                    switch (toolSettings) {
                        case CustomTool.MSBuildTarget:
                            UpdateRccStepMsBuild(
                                vfc,
                                rccOpts,
                                filesInQrcFile,
                                nameOnly,
                                qrcCppFile);
                            break;
                        default:
                            UpdateRccStepCustomBuild(
                                vfc,
                                rccOpts,
                                filesInQrcFile,
                                nameOnly,
                                qrcCppFile);
                            break;
                    }
                }
                if (toolSettings == CustomTool.CustomBuildStep)
                    AddFileInFilter(Filters.GeneratedFiles(), qrcCppFile, true);
            } catch (Exception /*e*/) {
                Messages.PaneMessage(dteObject, "*** WARNING (RCC): Couldn't add rcc step");
            }
        }

        static public void ExcludeFromAllBuilds(VCFile file)
        {
            if (file == null)
                return;
            foreach (VCFileConfiguration conf in (IVCCollection) file.FileConfigurations) {
                if (!conf.ExcludedFromBuild)
                    conf.ExcludedFromBuild = true;
            }
        }

        bool IsCppMocFileCustomBuild(VCProject vcProj, VCFile vcFile, VCFile cppFile)
        {
            var mocFilePath = vcFile.FullPath;
            var cppFilePath = cppFile.FullPath;
            if (Path.GetDirectoryName(mocFilePath)
                != Path.GetDirectoryName(cppFilePath)) {
                return false;
            }

            if (Path.GetFileNameWithoutExtension(mocFilePath)
                != Path.GetFileNameWithoutExtension(cppFilePath)) {
                return false;
            }

            if (!string.Equals(Path.GetExtension(mocFilePath), ".cbt",
                StringComparison.InvariantCultureIgnoreCase)) {
                return false;
            }

            return true;
        }

        List<VCFile> GetCppMocOutputs(List<VCFile> mocFiles)
        {
            List<VCFile> outputFiles = new List<VCFile>();
            foreach (var mocFile in mocFiles) {
                foreach (VCFileConfiguration mocConfig
                    in (IVCCollection)mocFile.FileConfigurations) {

                    var cbtTool = HelperFunctions.GetCustomBuildTool(mocConfig);
                    if (cbtTool == null)
                        continue;
                    foreach (var output in cbtTool.Outputs.Split(new char[] { ';' })) {
                        string outputExpanded = output;
                        if (!HelperFunctions.ExpandString(ref outputExpanded, mocConfig))
                            continue;
                        string outputFullPath = "";
                        try {
                            outputFullPath = Path.GetFullPath(Path.Combine(
                                Path.GetDirectoryName(mocFile.FullPath),
                                outputExpanded));
                        } catch {
                            continue;
                        }
                        var vcFile = GetFileFromProject(outputFullPath);
                        if (vcFile != null)
                            outputFiles.Add(vcFile);
                    }
                }
            }
            return outputFiles;
        }

        List<VCFile> GetCppMocFiles(VCFile cppFile)
        {
            List<VCFile> mocFiles = new List<VCFile>();
            var vcProj = cppFile.project as VCProject;
            if (vcProj != null) {
                mocFiles.AddRange(from VCFile vcFile
                                  in (IVCCollection)vcProj.Files
                                  where vcFile.ItemType == "CustomBuild"
                                  && IsCppMocFileCustomBuild(vcProj, vcFile, cppFile)
                                  select vcFile);
                mocFiles.AddRange(GetCppMocOutputs(mocFiles));
            }
            return mocFiles;
        }

        bool IsCppMocFileQtMsBuild(VCProject vcProj, VCFile vcFile, VCFile cppFile)
        {
            foreach (VCFileConfiguration fileConfig in (IVCCollection)vcFile.FileConfigurations) {
                string inputFile = qtMsBuild.GetPropertyValue(fileConfig, QtMoc.Property.InputFile);
                HelperFunctions.ExpandString(ref inputFile, fileConfig);
                if (HelperFunctions.PathIsRelativeTo(inputFile, cppFile.ItemName))
                    return true;
            }
            return false;
        }

        bool HasCppMocFiles(VCFile cppFile)
        {
            if (!IsQtMsBuildEnabled()) {
                return File.Exists(Path.ChangeExtension(cppFile.FullPath, ".cbt"));
            } else {
                var vcProj = cppFile.project as VCProject;
                if (vcProj != null) {
                    foreach (VCFile vcFile in (IVCCollection)vcProj.Files) {
                        if (vcFile.ItemType == "CustomBuild") {
                            if (IsCppMocFileCustomBuild(vcProj, vcFile, cppFile))
                                return true;
                        } else if (vcFile.ItemType == QtMoc.ItemTypeName) {
                            if (IsCppMocFileQtMsBuild(vcProj, vcFile, cppFile))
                                return true;
                        }
                    }
                }
                return false;
            }
        }

        public void RemoveMocStep(VCFile file)
        {
            if (file.ItemType == QtMoc.ItemTypeName) {
                RemoveMocStepQtMsBuild(file);
            } else if (HelperFunctions.IsHeaderFile(file.Name)) {
                if (file.ItemType == "CustomBuild")
                    RemoveMocStepCustomBuild(file);
            } else {
                foreach (VCFile vcFile in (IVCCollection)vcPro.Files) {
                    if (vcFile.ItemType == QtMoc.ItemTypeName) {
                        if (IsCppMocFileQtMsBuild(vcPro, vcFile, file)) {
                            RemoveMocStepQtMsBuild(vcFile);
                        }
                    } else if (vcFile.ItemType == "CustomBuild") {
                        if (IsCppMocFileCustomBuild(vcPro, vcFile, file)) {
                            RemoveMocStepCustomBuild(file);
                            return;
                        }
                    }
                }
            }
        }

        public void RemoveMocStepQtMsBuild(VCFile file)
        {
            if (HelperFunctions.IsHeaderFile(file.Name)) {
                file.ItemType = "ClInclude";
            } else if (HelperFunctions.IsSourceFile(file.Name)) {
                file.ItemType = "ClCompile";
            } else {
                file.ItemType = "None";
            }
        }

        /// <summary>
        /// Removes the custom build step of a given file.
        /// </summary>
        /// <param name="file">file</param>
        public void RemoveMocStepCustomBuild(VCFile file)
        {
            try {
                if (!HasMocStep(file))
                    return;

                if (HelperFunctions.IsHeaderFile(file.Name)) {
                    foreach (VCFileConfiguration config in (IVCCollection) file.FileConfigurations) {
                        var tool = HelperFunctions.GetCustomBuildTool(config);
                        if (tool == null)
                            continue;

                        var cmdLine = tool.CommandLine;
                        if (cmdLine.Length > 0) {
                            var rex = new Regex(@"(\S*moc.exe|""\S+:\\\.*moc.exe"")");
                            while (true) {
                                var m = rex.Match(cmdLine);
                                if (!m.Success)
                                    break;

                                var start = m.Index;
                                var end = cmdLine.IndexOf("&&", start, StringComparison.Ordinal);
                                var a = cmdLine.IndexOf("\r\n", start, StringComparison.Ordinal);
                                if ((a > -1 && a < end) || (end < 0 && a > -1))
                                    end = a;
                                if (end < 0)
                                    end = cmdLine.Length;

                                cmdLine = cmdLine.Remove(start, end - start).Trim();
                                if (cmdLine.StartsWith("&&", StringComparison.Ordinal))
                                    cmdLine = cmdLine.Remove(0, 2).Trim();
                            }
                            tool.CommandLine = cmdLine;
                        }

                        var reg = new Regex("Moc'ing .+\\.\\.\\.");
                        var addDepends = tool.AdditionalDependencies;
                        addDepends = Regex.Replace(addDepends,
                            @"(\S*moc.exe|""\S+:\\\.*moc.exe"")", string.Empty);
                        addDepends = addDepends.Replace(file.RelativePath, string.Empty);
                        tool.AdditionalDependencies = string.Empty;
                        tool.Description = reg.Replace(tool.Description, string.Empty);
                        tool.Description = tool.Description.Replace("MOC " + file.Name, string.Empty);
                        var baseFileName = file.Name.Remove(file.Name.LastIndexOf('.'));
                        var pattern = "(\"(.*\\\\" + GetMocFileName(file.FullPath)
                            + ")\"|(\\S*" + GetMocFileName(file.FullPath) + "))";
                        string outputMocFile = null;
                        var regExp = new Regex(pattern);
                        tool.Outputs = tool.Outputs.Replace(ProjectMacros.Name, baseFileName);
                        var matchList = regExp.Matches(tool.Outputs);
                        if (matchList.Count > 0) {
                            if (matchList[0].Length > 0)
                                outputMocFile = matchList[0].ToString();
                            else if (matchList[1].Length > 1)
                                outputMocFile = matchList[1].ToString();
                        }
                        tool.Outputs = Regex.Replace(tool.Outputs,
                            pattern, string.Empty, RegexOptions.Multiline | RegexOptions.IgnoreCase);
                        tool.Outputs = Regex.Replace(tool.Outputs,
                            @"\s*;\s*;\s*", ";", RegexOptions.Multiline);
                        tool.Outputs = Regex.Replace(tool.Outputs,
                            @"(^\s*;|\s*;\s*$)", string.Empty, RegexOptions.Multiline);

                        if (outputMocFile != null) {
                            if (outputMocFile.StartsWith("\"", StringComparison.Ordinal))
                                outputMocFile = outputMocFile.Substring(1);
                            if (outputMocFile.EndsWith("\"", StringComparison.Ordinal))
                                outputMocFile = outputMocFile.Substring(0, outputMocFile.Length - 1);
                            HelperFunctions.ExpandString(ref outputMocFile, config);
                        }
                        var mocFile = GetFileFromProject(outputMocFile);
                        if (mocFile != null)
                            RemoveFileFromFilter(mocFile, Filters.GeneratedFiles());
                    }
                } else {
                    foreach (var mocFile in GetCppMocFiles(file)) {
                        RemoveFileFromFilter(mocFile, Filters.GeneratedFiles());
                    }
                }
            } catch {
                throw new QtVSException(SR.GetString("QtProject_CannotRemoveMocStep", file.FullPath));
            }
        }

        public List<VCFile> GetResourceFiles()
        {
            var qrcFiles = new List<VCFile>();

            foreach (VCFile f in (IVCCollection) VCProject.Files) {
                if (HelperFunctions.IsQrcFile(f.Name))
                    qrcFiles.Add(f);
            }
            return qrcFiles;
        }

        /// <summary>
        /// Returns the file if it can be found, otherwise null.
        /// </summary>
        /// <param name="filter">filter name</param>
        /// <param name="fileName">relative file path to the project</param>
        /// <returns></returns>
        public VCFile GetFileFromFilter(FakeFilter filter, string fileName)
        {
            var vcfilter = FindFilterFromGuid(filter.UniqueIdentifier);

            // try with name as well
            if (vcfilter == null)
                vcfilter = FindFilterFromName(filter.Name);

            if (vcfilter == null)
                return null;

            try {
                FileInfo fi = null;
                if (Path.IsPathRooted(fileName))
                    fi = new FileInfo(fileName);
                else
                    fi = new FileInfo(ProjectDir + "\\" + fileName);

                foreach (VCFile file in (IVCCollection) vcfilter.Files) {
                    if (file.MatchName(fi.FullName, true))
                        return file;
                }
            } catch { }
            return null;
        }

        /// <summary>
        /// Returns the file (VCFile) specified by the file name from a given
        /// project.
        /// </summary>
        /// <param name="fileName">file name (relative path)</param>
        /// <returns></returns>
        public VCFile GetFileFromProject(string fileName)
        {
            fileName = HelperFunctions.NormalizeRelativeFilePath(fileName);

            var nf = fileName;
            if (!HelperFunctions.IsAbsoluteFilePath(fileName))
                nf = HelperFunctions.NormalizeFilePath(vcPro.ProjectDirectory + "\\" + fileName);
            nf = nf.ToLower();

            foreach (VCFile f in (IVCCollection) vcPro.Files) {
                if (f.FullPath.ToLower() == nf)
                    return f;
            }
            return null;
        }

        /// <summary>
        /// Returns the files specified by the file name from a given project as list of VCFile
        /// objects.
        /// </summary>
        /// <param name="fileName">file name (relative path)</param>
        /// <returns></returns>
        public IEnumerable<VCFile> GetFilesFromProject(string fileName)
        {
            var fi = new FileInfo(HelperFunctions.NormalizeRelativeFilePath(fileName));
            foreach (VCFile f in (IVCCollection) vcPro.Files) {
                if (string.Equals(f.Name, fi.Name, StringComparison.OrdinalIgnoreCase))
                    yield return f;
            }
        }

        private static List<VCFile> GetAllFilesFromFilter(VCFilter filter)
        {
            var tmpList = ((IVCCollection) filter.Files).Cast<VCFile>().ToList();
            foreach (VCFilter subfilter in (IVCCollection) filter.Filters)
                tmpList.AddRange(GetAllFilesFromFilter(subfilter));
            return tmpList;
        }

        /// <summary>
        /// Adds a file to a filter. If the filter doesn't exist yet, it
        /// will be created. (Doesn't check for duplicates)
        /// </summary>
        /// <param name="filter">fake filter</param>
        /// <param name="fileName">relative file name</param>
        /// <returns>A VCFile object of the added file.</returns>
        public VCFile AddFileInFilter(FakeFilter filter, string fileName)
        {
            return AddFileInFilter(filter, fileName, false);
        }

        public void RemoveItem(ProjectItem item)
        {
            foreach (ProjectItem tmpFilter in Project.ProjectItems) {
                if (tmpFilter.Name == item.Name) {
                    tmpFilter.Remove();
                    return;
                }
                foreach (ProjectItem tmpItem in tmpFilter.ProjectItems) {
                    if (tmpItem.Name == item.Name) {
                        tmpItem.Remove();
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Adds a file to a filter. If the filter doesn't exist yet, it
        /// will be created.
        /// </summary>
        /// <param name="filter">fake filter</param>
        /// <param name="fileName">relative file name</param>
        /// <param name="checkForDuplicates">true if we don't want duplicated files</param>
        /// <returns>A VCFile object of the added file.</returns>
        public VCFile AddFileInFilter(FakeFilter filter, string fileName, bool checkForDuplicates)
        {
            return AddFileInSubfilter(filter, null, fileName, checkForDuplicates);
        }

        public VCFile AddFileInSubfilter(FakeFilter filter, string subfilterName, string fileName)
        {
            return AddFileInSubfilter(filter, subfilterName, fileName, false);
        }

        public VCFile AddFileInSubfilter(FakeFilter filter, string subfilterName, string fileName, bool checkForDuplicates)
        {
            try {
                var vfilt = FindFilterFromGuid(filter.UniqueIdentifier);
                if (vfilt == null) {
                    if (!vcPro.CanAddFilter(filter.Name)) {
                        // check if user already created this filter... then add guid
                        vfilt = FindFilterFromName(filter.Name);
                        if (vfilt == null)
                            throw new QtVSException(SR.GetString("QtProject_CannotAddFilter", filter.Name));
                    } else {
                        vfilt = (VCFilter) vcPro.AddFilter(filter.Name);
                    }

                    vfilt.UniqueIdentifier = filter.UniqueIdentifier;
                    vfilt.Filter = filter.Filter;
                    vfilt.ParseFiles = filter.ParseFiles;
                }

                if (!string.IsNullOrEmpty(subfilterName)) {
                    var lowerSubFilterName = subfilterName.ToLower();
                    var subfilterFound = false;
                    foreach (VCFilter subfilt in vfilt.Filters as IVCCollection) {
                        if (subfilt.Name.ToLower() == lowerSubFilterName) {
                            vfilt = subfilt;
                            subfilterFound = true;
                            break;
                        }
                    }
                    if (subfilterFound) {
                        // Do filter names differ in upper/lower case?
                        if (subfilterName != vfilt.Name) {
                            try {
                                // Try to rename the filter for aesthetic reasons.
                                vfilt.Name = subfilterName;
                            } catch {
                                // Renaming didn't work. We don't care.
                            }
                        }
                    }
                    if (!subfilterFound) {
                        if (!vfilt.CanAddFilter(subfilterName))
                            throw new QtVSException(SR.GetString("QtProject_CannotAddFilter", filter.Name));

#if !VS2017
                        // TODO: Enable once the freeze gets fixed in VS.
                        vfilt = (VCFilter) vfilt.AddFilter(subfilterName);
                        vfilt.Filter = "cpp;moc";
                        vfilt.SourceControlFiles = false;
#endif
                    }
                }

                if (checkForDuplicates) {
                    // check if file exists in filter already
                    var vcFile = GetFileFromFilter(filter, fileName);
                    if (vcFile != null)
                        return vcFile;
                }

                if (vfilt.CanAddFile(fileName))
                    return (VCFile) (vfilt.AddFile(fileName));
                throw new QtVSException(SR.GetString("QtProject_CannotAddFile", fileName));
            } catch {
                throw new QtVSException(SR.GetString("QtProject_CannotAddFile", fileName));
            }
        }

        /// <summary>
        /// Removes a file from the filter.
        /// This file will be deleted!
        /// </summary>
        /// <param name="file">file</param>
        public void RemoveFileFromFilter(VCFile file, FakeFilter filter)
        {
            try {
                var vfilt = FindFilterFromGuid(filter.UniqueIdentifier);

                if (vfilt == null)
                    vfilt = FindFilterFromName(filter.Name);

                if (vfilt == null)
                    return;

                RemoveFileFromFilter(file, vfilt);
            } catch {
                throw new QtVSException(SR.GetString("QtProject_CannotRemoveFile", file.Name));
            }
        }

        /// <summary>
        /// Removes a file from the filter.
        /// This file will be deleted!
        /// </summary>
        /// <param name="file">file</param>
        public void RemoveFileFromFilter(VCFile file, VCFilter filter)
        {
            try {
                filter.RemoveFile(file);
                var fi = new FileInfo(file.FullPath);
                if (fi.Exists)
                    fi.Delete();
            } catch {
            }

            var subfilters = (IVCCollection) filter.Filters;
            for (var i = subfilters.Count; i > 0; i--) {
                try {
                    var subfilter = (VCFilter) subfilters.Item(i);
                    RemoveFileFromFilter(file, subfilter);
                } catch {
                }
            }
        }

        public void MoveFileToDeletedFolder(VCFile vcfile)
        {
            var srcFile = new FileInfo(vcfile.FullPath);

            if (!srcFile.Exists)
                return;

            var destFolder = vcPro.ProjectDirectory + "\\Deleted\\";
            var destName = destFolder + vcfile.Name.Replace(".", "_") + ".bak";
            var fileNr = 0;

            try {
                if (!Directory.Exists(destFolder))
                    Directory.CreateDirectory(destFolder);

                while (File.Exists(destName)) {
                    fileNr++;
                    destName = destName.Substring(0, destName.LastIndexOf('.')) + ".b";
                    destName += fileNr.ToString("00");
                }

                srcFile.MoveTo(destName);
            } catch (Exception e) {
                Messages.DisplayWarningMessage(e, SR.GetString("QtProject_DeletedFolderFullOrProteced"));
            }
        }

        public VCFilter FindFilterFromName(string filtername)
        {
            try {
                foreach (VCFilter vcfilt in (IVCCollection) vcPro.Filters) {
                    if (vcfilt.Name.ToLower() == filtername.ToLower())
                        return vcfilt;
                }
                return null;
            } catch {
                throw new QtVSException(SR.GetString("QtProject_CannotFindFilter"));
            }
        }

        public VCFilter FindFilterFromGuid(string filterguid)
        {
            try {
                foreach (VCFilter vcfilt in (IVCCollection) vcPro.Filters) {
                    if (vcfilt.UniqueIdentifier != null
                        && vcfilt.UniqueIdentifier.ToLower() == filterguid.ToLower()) {
                        return vcfilt;
                    }
                }
                return null;
            } catch {
                throw new QtVSException(SR.GetString("QtProject_CannotFindFilter"));
            }
        }

        public VCFilter AddFilterToProject(FakeFilter filter)
        {
            try {
                var vfilt = FindFilterFromGuid(filter.UniqueIdentifier);
                if (vfilt == null) {
                    if (!vcPro.CanAddFilter(filter.Name)) {
                        vfilt = FindFilterFromName(filter.Name);
                        if (vfilt == null)
                            throw new QtVSException(SR.GetString("QtProject_ProjectCannotAddFilter", filter.Name));
                    } else {
                        vfilt = (VCFilter) vcPro.AddFilter(filter.Name);
                    }

                    vfilt.UniqueIdentifier = filter.UniqueIdentifier;
                    vfilt.Filter = filter.Filter;
                    vfilt.ParseFiles = filter.ParseFiles;
                }
                return vfilt;
            } catch {
                throw new QtVSException(SR.GetString("QtProject_ProjectCannotAddResourceFilter"));
            }
        }

        public void AddDirectories()
        {
            try {
                // resource directory
                var fi = new FileInfo(envPro.FullName);
                var dfi = new DirectoryInfo(fi.DirectoryName + "\\" + Resources.resourceDir);
                dfi.Create();

                // generated files directory
                dfi = new DirectoryInfo(fi.DirectoryName + "\\" + Resources.generatedFilesDir);
                dfi.Create();
            } catch {
                throw new QtVSException(SR.GetString("QtProject_CannotCreateResourceDir"));
            }
            AddFilterToProject(Filters.ResourceFiles());
        }

        public void Finish()
        {
            try {
                var solutionExplorer = dte.Windows.Item(Constants.vsWindowKindSolutionExplorer);
                if (solutionExplorer != null) {
                    var hierarchy = (UIHierarchy) solutionExplorer.Object;
                    var projects = hierarchy.UIHierarchyItems.Item(1).UIHierarchyItems;

                    foreach (UIHierarchyItem itm in projects) {
                        if (itm.Name == envPro.Name) {
                            foreach (UIHierarchyItem i in itm.UIHierarchyItems) {
                                if (i.Name == Filters.GeneratedFiles().Name)
                                    i.UIHierarchyItems.Expanded = false;
                            }
                            break;
                        }
                    }
                }
            } catch { }
        }

        public bool IsDesignerPluginProject()
        {
            var b = false;
            if (Project.Globals.get_VariablePersists("IsDesignerPlugin")) {
                var s = (string) Project.Globals["IsDesignerPlugin"];
                try {
                    b = bool.Parse(s);
                } catch { }
            }
            return b;
        }

        /// <summary>
        /// Adds a file to a specified filter in a project.
        /// </summary>
        /// <param name="destName">name of the file in the project (relative to the project directory)</param>
        /// <param name="filter">filter</param>
        /// <returns>VCFile</returns>
        public VCFile AddFileToProject(string destName, FakeFilter filter)
        {
            VCFile file = null;
            if (filter != null)
                file = AddFileInFilter(filter, destName);
            else
                file = (VCFile) vcPro.AddFile(destName);

            if (file == null)
                return null;

            if (HelperFunctions.IsHeaderFile(file.Name)) {
                foreach (VCConfiguration config in (IVCCollection) vcPro.Configurations) {
                    var compiler = CompilerToolWrapper.Create(config);
                    if (compiler == null)
                        continue;

                    var paths = compiler.GetAdditionalIncludeDirectoriesList();
                    var fi = new FileInfo(file.FullPath);
                    var relativePath = HelperFunctions.GetRelativePath(ProjectDir, fi.Directory.ToString());
                    var fixedRelativePath = FixFilePathForComparison(relativePath);
                    if (!paths.Any(p => FixFilePathForComparison(p) == fixedRelativePath))
                        compiler.AddAdditionalIncludeDirectories(relativePath);
                }
            }
            return file;
        }

        /// <summary>
        /// adjusts the whitespaces, tabs in the given file according to VS settings
        /// </summary>
        /// <param name="file"></param>
        public void AdjustWhitespace(string file)
        {
            if (!File.Exists(file))
                return;

            // only replace whitespaces in known types
            if (!HelperFunctions.IsSourceFile(file) && !HelperFunctions.IsHeaderFile(file)
                && !HelperFunctions.IsUicFile(file)) {
                return;
            }

            try {
                var prop = dte.get_Properties("TextEditor", "C/C++");
                var tabSize = Convert.ToInt64(prop.Item("TabSize").Value);
                var insertTabs = Convert.ToBoolean(prop.Item("InsertTabs").Value);

                var oldValue = insertTabs ? "    " : "\t";
                var newValue = insertTabs ? "\t" : GetWhitespaces(tabSize);

                var list = new List<string>();
                var reader = new StreamReader(file);
                var line = reader.ReadLine();
                while (line != null) {
                    if (line.StartsWith(oldValue, StringComparison.Ordinal))
                        line = line.Replace(oldValue, newValue);
                    list.Add(line);
                    line = reader.ReadLine();
                }
                reader.Close();

                var writer = new StreamWriter(file);
                foreach (var l in list)
                    writer.WriteLine(l);
                writer.Close();
            } catch (Exception e) {
                Messages.PaneMessage(dte, SR.GetString("QtProject_CannotAdjustWhitespaces",
                    e.ToString()));
            }
        }

        private static string GetWhitespaces(long size)
        {
            var whitespaces = string.Empty;
            for (long i = 0; i < size; ++i)
                whitespaces += " ";
            return whitespaces;
        }

        /// <summary>
        /// Copy a file to the projects folder. Does not add the file to the project.
        /// </summary>
        /// <param name="srcFile">full name of the file to add</param>
        /// <param name="destFolder">the name of the project folder</param>
        /// <param name="destName">name of the file in the project (relative to the project directory)</param>
        /// <returns>full name of the destination file</returns>
        public static string CopyFileToFolder(string srcFile, string destFolder, string destName)
        {
            var fullDestName = destFolder + "\\" + destName;
            var fi = new FileInfo(fullDestName);

            var replace = true;
            if (File.Exists(fullDestName)) {
                if (DialogResult.No == MessageBox.Show(SR.GetString("QtProject_FileExistsInProjectFolder", destName)
                    , SR.GetString("Resources_QtVsTools"), MessageBoxButtons.YesNo, MessageBoxIcon.Question)) {
                    replace = false;
                }
            }

            if (replace) {
                if (!fi.Directory.Exists)
                    fi.Directory.Create();
                File.Copy(srcFile, fullDestName, true);
                var attribs = File.GetAttributes(fullDestName);
                File.SetAttributes(fullDestName, attribs & (~FileAttributes.ReadOnly));
            }
            return fi.FullName;
        }

        public static void ReplaceTokenInFile(string file, string token, string replacement)
        {
            var text = string.Empty;
            try {
                var reader = new StreamReader(file);
                text = reader.ReadToEnd();
                reader.Close();
            } catch (Exception e) {
                Messages.DisplayErrorMessage(
                    SR.GetString("QtProject_CannotReplaceTokenRead", token, replacement, e.ToString()));
                return;
            }

            try {
                if (token.ToUpper() == "%PRE_DEF%" && !Char.IsLetter(replacement[0]))
                    replacement = "_" + replacement;

                text = text.Replace(token, replacement);
                var writer = new StreamWriter(file);
                writer.Write(text);
                writer.Close();
            } catch (Exception e) {
                Messages.DisplayErrorMessage(
                    SR.GetString("QtProject_CannotReplaceTokenWrite", token, replacement, e.ToString()));
            }
        }

        public void RepairGeneratedFilesStructure()
        {
            DeleteGeneratedFiles();

            var files = new ConcurrentBag<VCFile>();
            Task.WaitAll(
                Task.Run(() =>
                    Parallel.ForEach(((IVCCollection) vcPro.Files).Cast<VCFile>(), file =>
                    {
                        var name = file.Name;
                        if (!HelperFunctions.IsHeaderFile(name) && !HelperFunctions.IsSourceFile(name))
                            return;
                        if (HelperFunctions.HasQObjectDeclaration(file))
                            files.Add(file);
                    })
                )
            );

            qtMsBuild.BeginSetItemProperties();
            foreach (var file in files) {
                RemoveMocStep(file);
                AddMocStep(file);
            }
            qtMsBuild.EndSetItemProperties();
        }

        public void TranslateFilterNames()
        {
            var filters = vcPro.Filters as IVCCollection;
            if (filters == null)
                return;

            foreach (VCFilter filter in filters) {
                if (filter.Name == "Form Files")
                    filter.Name = Filters.FormFiles().Name;
                if (filter.Name == "Generated Files")
                    filter.Name = Filters.GeneratedFiles().Name;
                if (filter.Name == "Header Files")
                    filter.Name = Filters.HeaderFiles().Name;
                if (filter.Name == "Resource Files")
                    filter.Name = Filters.ResourceFiles().Name;
                if (filter.Name == "Source Files")
                    filter.Name = Filters.SourceFiles().Name;
            }
        }

        public string CreateQrcFile(string className, string destName)
        {
            var fullDestName = vcPro.ProjectDirectory + "\\" + destName;

            if (!File.Exists(fullDestName)) {
                FileStream s = null;
                try {
                    s = File.Open(fullDestName, FileMode.CreateNew);
                    if (s.CanWrite) {
                        using (var sw = new StreamWriter(s)) {
                            s = null;
                            sw.WriteLine("<RCC>");
                            sw.WriteLine("    <qresource prefix=\"" + className + "\">");
                            sw.WriteLine("    </qresource>");
                            sw.WriteLine("</RCC>");
                        }
                    }
                } finally {
                    if (s != null)
                        s.Dispose();
                }
                var attribs = File.GetAttributes(fullDestName);
                File.SetAttributes(fullDestName, attribs & (~FileAttributes.ReadOnly));
            }

            var fi = new FileInfo(fullDestName);
            return fi.FullName;
        }

        public void AddActiveQtBuildStep(string version, string defFile = null)
        {
            foreach (VCConfiguration config in (IVCCollection) vcPro.Configurations) {
                var idlFile = "\"$(IntDir)/" + envPro.Name + ".idl\"";
                var tblFile = "\"$(IntDir)/" + envPro.Name + ".tlb\"";

                var tool = (VCPostBuildEventTool) ((IVCCollection) config.Tools).Item("VCPostBuildEventTool");
                var idc = "$(QTDIR)\\bin\\idc.exe \"$(TargetPath)\" /idl " + idlFile + " -version " + version;
                var midl = "midl " + idlFile + " /tlb " + tblFile;
                var idc2 = "$(QTDIR)\\bin\\idc.exe \"$(TargetPath)\" /tlb " + tblFile;
                var idc3 = "$(QTDIR)\\bin\\idc.exe \"$(TargetPath)\" /regserver";

                tool.CommandLine = idc + "\r\n" + midl + "\r\n" + idc2 + "\r\n" + idc3;
                tool.Description = string.Empty;

                var linker = (VCLinkerTool) ((IVCCollection) config.Tools).Item("VCLinkerTool");
                var librarian = (VCLibrarianTool) ((IVCCollection) config.Tools).Item("VCLibrarianTool");

                if (linker != null) {
                    linker.Version = version;
                    linker.ModuleDefinitionFile = defFile ?? envPro.Name + ".def";
                } else {
                    librarian.ModuleDefinitionFile = defFile ?? envPro.Name + ".def";
                }
            }
        }

        private void UpdateCompilerIncludePaths(string oldDir, string newDir)
        {
            var fixedOldDir = FixFilePathForComparison(oldDir);
            var dirs = new[] {
                FixFilePathForComparison(QtVSIPSettings.GetUicDirectory(envPro)),
                FixFilePathForComparison(QtVSIPSettings.GetMocDirectory(envPro)),
                FixFilePathForComparison(QtVSIPSettings.GetRccDirectory(envPro))
            };

            var oldDirIsUsed = dirs.Any(dir => dir == fixedOldDir);

            var incList = new List<string>();
            foreach (VCConfiguration config in (IVCCollection) vcPro.Configurations) {
                var compiler = CompilerToolWrapper.Create(config);
                if (compiler == null)
                    continue;
                var paths = compiler.AdditionalIncludeDirectories;
                if (paths.Count == 0)
                    continue;

                if (!oldDirIsUsed) {
                    for (var i = paths.Count - 1; i >= 0; --i) {
                        if (FixFilePathForComparison(paths[i]) == fixedOldDir)
                            paths.RemoveAt(i);
                    }
                }
                incList.Clear();
                foreach (var path in paths) {
                    var tmp = HelperFunctions.NormalizeRelativeFilePath(path);
                    if (tmp.Length > 0 && !incList.Contains(tmp))
                        incList.Add(tmp);
                }
                var alreadyThere = false;
                var fixedNewDir = FixFilePathForComparison(newDir);
                foreach (var include in incList) {
                    if (FixFilePathForComparison(include) == fixedNewDir) {
                        alreadyThere = true;
                        break;
                    }
                }
                if (!alreadyThere)
                    incList.Add(HelperFunctions.NormalizeRelativeFilePath(newDir));

                compiler.AdditionalIncludeDirectories = incList;
            }
        }

        private static string FixFilePathForComparison(string path)
        {
            path = HelperFunctions.NormalizeRelativeFilePath(path);
            return path.ToLower();
        }

        public void UpdateUicSteps(string oldUicDir, bool update_inc_path)
        {
            Messages.PaneMessage(dte, "\r\n=== Update uic steps ===");
            var vcFilter = FindFilterFromGuid(Filters.GeneratedFiles().UniqueIdentifier);
            if (vcFilter != null) {
                var filterFiles = (IVCCollection) vcFilter.Files;
                for (var i = filterFiles.Count; i > 0; i--) {
                    var file = (VCFile) filterFiles.Item(i);
                    if (file.Name.StartsWith("ui_", StringComparison.OrdinalIgnoreCase)) {
                        RemoveFileFromFilter(file, vcFilter);
                        HelperFunctions.DeleteEmptyParentDirs(file);
                    }
                }
            }

            var updatedFiles = 0;
            var j = 0;

            var files = new VCFile[((IVCCollection) vcPro.Files).Count];
            foreach (VCFile file in (IVCCollection) vcPro.Files)
                files[j++] = file;

            qtMsBuild.BeginSetItemProperties();
            foreach (var file in files) {
                if (HelperFunctions.IsUicFile(file.Name) && !IsUic3File(file)) {
                    AddUic4BuildStep(file);
                    Messages.PaneMessage(dte, "Update uic step for " + file.Name + ".");
                    ++updatedFiles;
                }
            }
            qtMsBuild.EndSetItemProperties();
            if (update_inc_path)
                UpdateCompilerIncludePaths(oldUicDir, QtVSIPSettings.GetUicDirectory(envPro));

            Messages.PaneMessage(dte, "\r\n=== " + updatedFiles + " uic steps updated. ===\r\n");
        }

        private static bool IsUic3File(VCFile file)
        {
            foreach (VCFileConfiguration config in (IVCCollection) file.FileConfigurations) {
                var tool = HelperFunctions.GetCustomBuildTool(config);
                if (tool == null)
                    return false;
                if (tool.CommandLine.IndexOf("uic3.exe", StringComparison.OrdinalIgnoreCase) > -1)
                    return true;
            }
            return false;
        }

        public bool UsePrecompiledHeaders(VCConfiguration config)
        {
            var compiler = CompilerToolWrapper.Create(config);
            return UsePrecompiledHeaders(compiler);
        }

        private bool UsePrecompiledHeaders(CompilerToolWrapper compiler)
        {
            try {
                compiler.SetUsePrecompiledHeader(pchOption.pchUseUsingSpecific);
                var pcHeaderThrough = GetPrecompiledHeaderThrough();
                if (string.IsNullOrEmpty(pcHeaderThrough))
                    pcHeaderThrough = "stdafx.h";
                compiler.SetPrecompiledHeaderThrough(pcHeaderThrough);
                var pcHeaderFile = GetPrecompiledHeaderFile();
                if (string.IsNullOrEmpty(pcHeaderFile))
                    pcHeaderFile = ".\\$(ConfigurationName)/" + Project.Name + ".pch";
                compiler.SetPrecompiledHeaderFile(pcHeaderFile);
                return true;
            } catch {
                return false;
            }
        }

        public bool UsesPrecompiledHeaders()
        {
            foreach (VCConfiguration config in vcPro.Configurations as IVCCollection) {
                if (!UsesPrecompiledHeaders(config))
                    return false;
            }
            return true;
        }

        public static bool UsesPrecompiledHeaders(VCConfiguration config)
        {
            var compiler = CompilerToolWrapper.Create(config);
            return UsesPrecompiledHeaders(compiler);
        }

        private static bool UsesPrecompiledHeaders(CompilerToolWrapper compiler)
        {
            try {
                if (compiler.GetUsePrecompiledHeader() != pchOption.pchNone)
                    return true;
            } catch { }
            return false;
        }

        public string GetPrecompiledHeaderThrough()
        {
            foreach (VCConfiguration config in vcPro.Configurations as IVCCollection) {
                var header = GetPrecompiledHeaderThrough(config);
                if (header != null)
                    return header;
            }
            return null;
        }

        public static string GetPrecompiledHeaderThrough(VCConfiguration config)
        {
            var compiler = CompilerToolWrapper.Create(config);
            return GetPrecompiledHeaderThrough(compiler);
        }

        private static string GetPrecompiledHeaderThrough(CompilerToolWrapper compiler)
        {
            try {
                var header = compiler.GetPrecompiledHeaderThrough();
                if (!string.IsNullOrEmpty(header))
                    return header.ToLower();
            } catch { }
            return null;
        }

        public string GetPrecompiledHeaderFile()
        {
            foreach (VCConfiguration config in vcPro.Configurations as IVCCollection) {
                var file = GetPrecompiledHeaderFile(config);
                if (!string.IsNullOrEmpty(file))
                    return file;
            }
            return null;
        }

        public static string GetPrecompiledHeaderFile(VCConfiguration config)
        {
            var compiler = CompilerToolWrapper.Create(config);
            return GetPrecompiledHeaderFile(compiler);
        }

        private static string GetPrecompiledHeaderFile(CompilerToolWrapper compiler)
        {
            try {
                var file = compiler.GetPrecompiledHeaderFile();
                if (!string.IsNullOrEmpty(file))
                    return file;
            } catch { }
            return null;
        }

        public static void SetPCHOption(VCFile vcFile, pchOption option)
        {
            foreach (VCFileConfiguration config in vcFile.FileConfigurations as IVCCollection) {
                var compiler = CompilerToolWrapper.Create(config);
                compiler.SetUsePrecompiledHeader(option);
            }
        }

        private static VCFileConfiguration GetVCFileConfigurationByName(VCFile file, string configName)
        {
            foreach (VCFileConfiguration cfg in (IVCCollection) file.FileConfigurations) {
                if (cfg.Name == configName)
                    return cfg;
            }
            return null;
        }

        /// <summary>
        /// Searches for the generated file inside the "Generated Files" filter.
        /// The function looks for the given filename and uses the fileConfig's
        /// ConfigurationName and Platform if moc directory contains $(ConfigurationName)
        /// and/or $(PlatformName).
        /// Otherwise it just uses the "Generated Files" filter
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="fileConfig"></param>
        /// <returns></returns>
        private VCFile GetGeneratedMocFile(string fileName, VCFileConfiguration fileConfig)
        {
            if (QtVSIPSettings.HasDifferentMocFilePerConfig(envPro)
                || QtVSIPSettings.HasDifferentMocFilePerPlatform(envPro)) {
                var projectConfig = (VCConfiguration) fileConfig.ProjectConfiguration;
                var configName = projectConfig.ConfigurationName;
                var platformName = ((VCPlatform) projectConfig.Platform).Name;
                var generatedFiles = FindFilterFromGuid(Filters.GeneratedFiles().UniqueIdentifier);
                if (generatedFiles == null)
                    return null;
                foreach (VCFilter filt in (IVCCollection) generatedFiles.Filters) {
                    if (filt.Name == configName + "_" + platformName ||
                        filt.Name == configName || filt.Name == platformName) {
                        foreach (VCFile filtFile in (IVCCollection) filt.Files) {
                            if (HelperFunctions.PathIsRelativeTo(filtFile.FullPath, fileName))
                                return filtFile;
                        }
                    }
                }

                //If a project from the an AddIn prior to 1.1.0 was loaded, the generated files are located directly
                //in the generated files filter.
                var relativeMocPath = QtVSIPSettings.GetMocDirectory(
                    envPro,
                    configName,
                    platformName,
                    fileConfig.File as VCFile)
                    + '\\' + fileName;
                //Remove .\ at the beginning of the mocPath
                if (relativeMocPath.StartsWith(".\\", StringComparison.Ordinal))
                    relativeMocPath = relativeMocPath.Remove(0, 2);
                foreach (VCFile filtFile in (IVCCollection) generatedFiles.Files) {
                    if (HelperFunctions.PathIsRelativeTo(filtFile.FullPath, relativeMocPath))
                        return filtFile;
                }
            } else {
                var generatedFiles = FindFilterFromGuid(Filters.GeneratedFiles().UniqueIdentifier);
                foreach (VCFile filtFile in (IVCCollection) generatedFiles.Files) {
                    if (HelperFunctions.PathIsRelativeTo(filtFile.FullPath, fileName))
                        return filtFile;
                }
            }
            return null;
        }

        public void RefreshQtMocIncludePath()
        {
            foreach (VCConfiguration config in (IVCCollection)vcPro.Configurations) {
                var propsClCompile = config.Rules.Item("CL") as IVCRulePropertyStorage;
                var propsQtMoc = config.Rules.Item(QtMoc.ItemTypeName) as IVCRulePropertyStorage;
                if (propsClCompile == null || propsQtMoc == null)
                    continue;
                propsQtMoc.SetPropertyValue(QtMoc.Property.IncludePath.ToString(),
                    propsClCompile.GetUnevaluatedPropertyValue("AdditionalIncludeDirectories"));
            }
        }

        public void RefreshQtMocDefine()
        {
            foreach (VCConfiguration config in (IVCCollection)vcPro.Configurations) {
                var propsClCompile = config.Rules.Item("CL") as IVCRulePropertyStorage;
                var propsQtMoc = config.Rules.Item(QtMoc.ItemTypeName) as IVCRulePropertyStorage;
                if (propsClCompile == null || propsQtMoc == null)
                    continue;
                propsQtMoc.SetPropertyValue(QtMoc.Property.Define.ToString(),
                    propsClCompile.GetUnevaluatedPropertyValue("PreprocessorDefinitions"));
            }
        }

        public void RefreshMocSteps()
        {
            var filesCollection = vcPro.Files as IVCCollection;
            if (filesCollection == null)
                return;

            int progress = 0;
            int progressTotal = filesCollection.Count;
            var waitDialog = WaitDialog.StartWithProgress(SR.GetString("Resources_QtVsTools"),
                SR.GetString("WaitDialogRefreshMoc"), null, null, 5, false,
                progressTotal, progress++);
            qtMsBuild.BeginSetItemProperties();
            foreach (VCFile vcfile in filesCollection) {
                RefreshMocStep(vcfile, false);
                waitDialog.Update(SR.GetString("WaitDialogRefreshMoc"), null, null,
                    progress++, progressTotal, true);
            }
            waitDialog.Stop();

            waitDialog = WaitDialog.Start(SR.GetString("Resources_QtVsTools"),
                SR.GetString("WaitDialogRefreshMoc"), null, null, 2, false, true);
            qtMsBuild.EndSetItemProperties();
            waitDialog.Stop();
        }

        public void RefreshMocStep(VCFile vcfile)
        {
            RefreshMocStep(vcfile, true);
        }

        /// <summary>
        /// Updates the moc command line for the given header or source file
        /// containing the Q_OBJECT macro.
        /// If the function is called from a property change for a single file
        /// (singleFile =  true) we may have to look for the according header
        /// file and refresh the moc step for this file, if it contains Q_OBJECT.
        /// </summary>
        /// <param name="vcfile"></param>
        private void RefreshMocStep(VCFile vcfile, bool singleFile)
        {
            var isHeaderFile = HelperFunctions.IsHeaderFile(vcfile.FullPath);
            if (!isHeaderFile && !HelperFunctions.IsSourceFile(vcfile.FullPath))
                return;

            if (mocCmdChecker == null)
                mocCmdChecker = new MocCmdChecker();

            foreach (VCFileConfiguration config in (IVCCollection) vcfile.FileConfigurations) {
                try {
                    string commandLine = "";
                    VCCustomBuildTool tool = null;
                    VCFile mocable = null;
                    var customBuildConfig = config;
                    if (isHeaderFile || vcfile.ItemType == QtMoc.ItemTypeName) {
                        mocable = vcfile;
                        if (vcfile.ItemType == "CustomBuild")
                            tool = HelperFunctions.GetCustomBuildTool(config);
                    } else {
                        var mocFileName = GetMocFileName(vcfile.FullPath);
                        var mocFile = GetGeneratedMocFile(mocFileName, config);
                        if (mocFile == null)
                            continue;

                        var mocFileConfig = GetVCFileConfigurationByName(mocFile, config.Name);
                        if (vcfile.ItemType == "CustomBuild")
                            tool = HelperFunctions.GetCustomBuildTool(mocFileConfig);
                        mocable = mocFile;
                        // It is possible that the function was called from a source file's property change, it is possible that
                        // we have to obtain the tool from the according header file
                        if ((vcfile.ItemType != "CustomBuild" || tool == null) && singleFile) {
                            var headerName = vcfile.FullPath.Remove(vcfile.FullPath.LastIndexOf('.')) + ".h";
                            mocFileName = GetMocFileName(headerName);
                            mocFile = GetGeneratedMocFile(mocFileName, config);
                            if (mocFile != null) {
                                mocable = GetFileFromProject(headerName);
                                customBuildConfig = GetVCFileConfigurationByName(mocable, config.Name);
                                if (mocable.ItemType == "CustomBuild")
                                    tool = HelperFunctions.GetCustomBuildTool(customBuildConfig);
                            }
                        }
                    }

                    if (mocable.ItemType == "CustomBuild") {
                        if (tool != null)
                            commandLine = tool.CommandLine;
                    } else if (mocable.ItemType == QtMoc.ItemTypeName) {
                        commandLine = qtMsBuild.GenerateQtMocCommandLine(customBuildConfig);
                    } else {
                        continue;
                    }

                    if ((mocable.ItemType == "CustomBuild" && tool == null)
                        || commandLine.IndexOf(
                            "moc.exe",
                            StringComparison.OrdinalIgnoreCase) == -1)
                        continue;

                    VCFile srcMocFile, cppFile;
                    if (vcfile.ItemType == QtMoc.ItemTypeName
                        && HelperFunctions.IsSourceFile(vcfile.ItemName)) {
                        srcMocFile = cppFile = vcfile;
                    } else {
                        srcMocFile = GetSourceFileForMocStep(mocable);
                        cppFile = GetCppFileForMocStep(mocable);
                    }
                    if (srcMocFile == null)
                        continue;
                    var mocableIsCPP = (srcMocFile == cppFile);

                    var cppItemType = (cppFile != null) ? cppFile.ItemType : "";
                    if (cppFile != null && cppItemType != "ClCompile")
                        cppFile.ItemType = "ClCompile";

                    string pchParameters = null;
                    VCFileConfiguration defineIncludeConfig = null;
                    CompilerToolWrapper compiler = null;
                    if (cppFile == null) {
                        // No file specific defines/includes
                        // but at least the project defines/includes are added
                        defineIncludeConfig = config;
                        compiler = CompilerToolWrapper.Create(config.ProjectConfiguration as VCConfiguration);
                    } else {
                        defineIncludeConfig = GetVCFileConfigurationByName(cppFile, config.Name);
                        compiler = CompilerToolWrapper.Create(defineIncludeConfig);
                    }

                    if (compiler != null && compiler.GetUsePrecompiledHeader() != pchOption.pchNone)
                        pchParameters = GetPCHMocOptions(srcMocFile, compiler);

                    var outputFileName = QtVSIPSettings.GetMocDirectory(envPro) + "\\";
                    if (mocableIsCPP) {
                        outputFileName += ProjectMacros.Name;
                        outputFileName += ".moc";
                    } else {
                        outputFileName += "moc_";
                        outputFileName += ProjectMacros.Name;
                        outputFileName += ".cpp";
                    }

                    var newCmdLine = mocCmdChecker.NewCmdLine(commandLine,
                        GetIncludes(defineIncludeConfig),
                        GetDefines(defineIncludeConfig),
                        QtVSIPSettings.GetMocOptions(envPro), srcMocFile.RelativePath,
                        pchParameters,
                        outputFileName);

                    if (cppFile != null && cppItemType != "ClCompile")
                        cppFile.ItemType = cppItemType;

                    // The tool's command line automatically gets a trailing "\r\n".
                    // We have to remove it to make the check below work.
                    var origCommandLine = commandLine;
                    if (origCommandLine.EndsWith("\r\n", StringComparison.Ordinal))
                        origCommandLine = origCommandLine.Substring(0, origCommandLine.Length - 2);

                    if (newCmdLine != null && newCmdLine != origCommandLine) {
                        // We have to delete the old moc file in order to trigger custom build step.
                        var configName = config.Name.Remove(config.Name.IndexOf('|'));
                        var platformName = config.Name.Substring(config.Name.IndexOf('|') + 1);
                        var projectPath = envPro.FullName.Remove(envPro.FullName.LastIndexOf('\\'));
                        var mocRelPath = GetRelativeMocFilePath(srcMocFile.FullPath, configName, platformName);
                        var mocPath = Path.Combine(projectPath, mocRelPath);
                        if (File.Exists(mocPath))
                            File.Delete(mocPath);
                        if (mocable.ItemType == "CustomBuild") {
                            tool.CommandLine = newCmdLine;
                        } else {
                            qtMsBuild.SetQtMocCommandLine(
                                customBuildConfig, newCmdLine, new VCMacroExpander(config));
                        }
                    }
                } catch {
                    Messages.PaneMessage(dte, "ERROR: failed to refresh moc step for " + vcfile.ItemName);
                }
            }
        }

        public void OnExcludedFromBuildChanged(VCFile vcFile, VCFileConfiguration vcFileCfg)
        {
            // Update the ExcludedFromBuild flags of the mocced file
            // according to the ExcludedFromBuild flag of the mocable source file.
            var moccedFileName = GetMocFileName(vcFile.Name);
            if (string.IsNullOrEmpty(moccedFileName))
                return;

            var moccedFile = GetGeneratedMocFile(moccedFileName, vcFileCfg);

            if (moccedFile != null) {
                VCFile cppFile = null;
                if (HelperFunctions.IsHeaderFile(vcFile.Name))
                    cppFile = GetCppFileForMocStep(vcFile);

                var moccedFileConfig = GetVCFileConfigurationByName(moccedFile, vcFileCfg.Name);
                if (moccedFileConfig != null) {
                    if (cppFile != null && IsMoccedFileIncluded(cppFile)) {
                        if (!moccedFileConfig.ExcludedFromBuild)
                            moccedFileConfig.ExcludedFromBuild = true;
                    } else if (moccedFileConfig.ExcludedFromBuild != vcFileCfg.ExcludedFromBuild) {
                        moccedFileConfig.ExcludedFromBuild = vcFileCfg.ExcludedFromBuild;
                    }
                }
            }
        }

        /// <summary>
        /// Helper function for RefreshMocStep.
        /// </summary>
        /// <param name="file"></param>
        /// <returns></returns>
        private VCFile GetSourceFileForMocStep(VCFile file)
        {
            if (HelperFunctions.IsHeaderFile(file.Name))
                return file;
            var fileName = file.Name;
            if (HelperFunctions.IsMocFile(fileName)) {
                fileName = fileName.Substring(0, fileName.Length - 4) + ".cpp";
                if (fileName.Length > 0) {
                    foreach (VCFile f in (IVCCollection) vcPro.Files) {
                        if (f.FullPath.EndsWith("\\" + fileName, StringComparison.Ordinal))
                            return f;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Helper function for Refresh/UpdateMocStep.
        /// </summary>
        /// <param name="file"></param>
        /// <returns></returns>
        private VCFile GetCppFileForMocStep(VCFile file)
        {
            string fileName = file.Name;
            if (fileName.EndsWith(".moc.cbt", StringComparison.OrdinalIgnoreCase))
                fileName = fileName.Remove(fileName.LastIndexOf('.'));
            if (HelperFunctions.IsHeaderFile(fileName) || HelperFunctions.IsMocFile(fileName)) {
                fileName = fileName.Remove(fileName.LastIndexOf('.')) + ".cpp";
                foreach (VCFile f in (IVCCollection) vcPro.Files) {
                    if (f.FullPath.EndsWith("\\" + fileName, StringComparison.Ordinal))
                        return f;
                }
            }
            return null;
        }

        public void UpdateMocSteps(string oldMocDir)
        {
            Messages.PaneMessage(dte, "\r\n=== Update moc steps ===");
            var orgFiles = new List<VCFile>();
            var abandonedMocFiles = new List<string>();
            var vcFilter = FindFilterFromGuid(Filters.GeneratedFiles().UniqueIdentifier);
            if (vcFilter != null) {
                var generatedFiles = GetAllFilesFromFilter(vcFilter);
                for (var i = generatedFiles.Count - 1; i >= 0; i--) {
                    var file = generatedFiles[i];
                    string fileName = null;
                    if (file.Name.StartsWith("moc_", StringComparison.OrdinalIgnoreCase) && !file.Name.EndsWith(".cbt", StringComparison.OrdinalIgnoreCase))
                        fileName = file.Name.Substring(4, file.Name.Length - 8) + ".h";
                    else if (HelperFunctions.IsMocFile(file.Name))
                        fileName = file.Name.Substring(0, file.Name.Length - 4) + ".cpp";

                    if (fileName != null) {
                        var found = false;
                        foreach (VCFile f in (IVCCollection) vcPro.Files) {
                            if (f.FullPath.EndsWith("\\" + fileName, StringComparison.OrdinalIgnoreCase)) {
                                if (!orgFiles.Contains(f) && HasMocStep(f, oldMocDir))
                                    orgFiles.Add(f);
                                found = true;
                            }
                        }
                        if (found) {
                            RemoveFileFromFilter(file, vcFilter);
                            HelperFunctions.DeleteEmptyParentDirs(file);
                        } else {
                            // We can't find foo.h for moc_foo.cpp or
                            // we can't find foo.cpp for foo.moc, thus we put the
                            // filename moc_foo.cpp / foo.moc into an error list.
                            abandonedMocFiles.Add(file.Name);
                        }
                    }
                }
            }

            UpdateCompilerIncludePaths(oldMocDir, QtVSIPSettings.GetMocDirectory(envPro));
            qtMsBuild.BeginSetItemProperties();
            foreach (var file in orgFiles) {
                try {
                    RemoveMocStep(file);
                    AddMocStep(file);
                } catch (QtVSException e) {
                    Messages.PaneMessage(dte, e.Message);
                    continue;
                }
                Messages.PaneMessage(dte, "Moc step updated successfully for " + file.Name + ".");
            }
            qtMsBuild.EndSetItemProperties();

            foreach (var s in abandonedMocFiles) {
                Messages.PaneMessage(dte, "Moc step update failed for " + s +
                    ". Reason: Could not determine source file for moccing.");
            }
            Messages.PaneMessage(dte, "\r\n=== Moc steps updated. Successful: " + orgFiles.Count
                + "   Failed: " + abandonedMocFiles.Count + " ===\r\n");

            CleanupFilter(vcFilter);
        }

        private void Clean()
        {
            var solutionConfigs = envPro.DTE.Solution.SolutionBuild.SolutionConfigurations;
            var backup = new List<KeyValuePair<SolutionContext, bool>>();
            foreach (SolutionConfiguration config in solutionConfigs) {
                var solutionContexts = config.SolutionContexts;
                if (solutionContexts == null)
                    continue;

                foreach (SolutionContext context in solutionContexts) {
                    backup.Add(new KeyValuePair<SolutionContext, bool>(context, context.ShouldBuild));
                    if (envPro.FullName.Contains(context.ProjectName)
                        && context.PlatformName == envPro.ConfigurationManager.ActiveConfiguration.PlatformName)
                        context.ShouldBuild = true;
                    else
                        context.ShouldBuild = false;
                }
            }
            try {
                envPro.DTE.Solution.SolutionBuild.Clean(true);
            } catch (System.Runtime.InteropServices.COMException) {
                // TODO: Implement some logging mechanism for exceptions.
            }

            foreach (var item in backup)
                item.Key.ShouldBuild = item.Value;
        }

        private void CleanupFilter(VCFilter filter)
        {
            var subFilters = filter.Filters as IVCCollection;
            if (subFilters == null)
                return;

            for (var i = subFilters.Count; i > 0; i--) {
                var subFilter = subFilters.Item(i) as VCFilter;
                var subFilterFilters = subFilter.Filters as IVCCollection;
                if (subFilterFilters == null)
                    continue;

                CleanupFilter(subFilter);

                var filterOrFileFound = false;
                foreach (var itemObject in subFilter.Items as IVCCollection) {
                    if (itemObject is VCFilter || itemObject is VCFile) {
                        filterOrFileFound = true;
                        break;
                    }
                }
                if (!filterOrFileFound)
                    filter.RemoveFilter(subFilter);
            }
        }

        public bool isWinRT()
        {
            try {
                var vcProject = Project.Object as VCProject;
                var vcConfigs = vcProject.Configurations as IVCCollection;
                var vcConfig = vcConfigs.Item(1) as VCConfiguration;
                var appType = vcConfig.GetEvaluatedPropertyValue("ApplicationType");
                if (appType == "Windows Store")
                    return true;
            } catch { }
            return false;
        }

        public bool PromptChangeQtVersion(string oldVersion, string newVersion)
        {
            var versionManager = QtVersionManager.The();
            var viOld = versionManager.GetVersionInfo(oldVersion);
            var viNew = versionManager.GetVersionInfo(newVersion);

            if (viOld == null || viNew == null)
                return true;

            var oldIsWinRt = viOld.isWinRT();
            var newIsWinRt = viNew.isWinRT();

            if (newIsWinRt == oldIsWinRt || newIsWinRt == isWinRT())
                return true;

            var promptCaption = string.Format("Change Qt Version ({0})", Project.Name);
            var promptText = string.Format(
                "Changing Qt version from {0} to {1}.\r\n" +
                "Project might not build. Are you sure?",
                newIsWinRt ? "Win32" : "WinRT",
                newIsWinRt ? "WinRT" : "Win32"
                );

            return (MessageBox.Show(
                promptText, promptCaption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                == DialogResult.Yes);
        }

        /// <summary>
        /// Changes the Qt version of this project.
        /// </summary>
        /// <param name="oldVersion">the current Qt version</param>
        /// <param name="newVersion">the new Qt version we want to change to</param>
        /// <param name="newProjectCreated">is set to true if a new Project object has been created</param>
        /// <returns>true, if the operation performed successfully</returns>
        public bool ChangeQtVersion(string oldVersion, string newVersion, ref bool newProjectCreated)
        {
            newProjectCreated = false;
            var versionManager = QtVersionManager.The();
            var viOld = versionManager.GetVersionInfo(oldVersion);
            var viNew = versionManager.GetVersionInfo(newVersion);

            string vsPlatformNameOld = null;
            if (viOld != null)
                vsPlatformNameOld = viOld.GetVSPlatformName();
            var vsPlatformNameNew = viNew.GetVSPlatformName();
            var bRefreshMocSteps = (vsPlatformNameNew != vsPlatformNameOld);

            try {
                if (vsPlatformNameOld != vsPlatformNameNew) {
                    if (!SelectSolutionPlatform(vsPlatformNameNew) || !HasPlatform(vsPlatformNameNew)) {
                        CreatePlatform(vsPlatformNameOld, vsPlatformNameNew, viOld, viNew, ref newProjectCreated);
                        bRefreshMocSteps = false;
                        UpdateMocSteps(QtVSIPSettings.GetMocDirectory(envPro));
                    }
                }
                var configManager = envPro.ConfigurationManager;
                if (configManager.ActiveConfiguration.PlatformName != vsPlatformNameNew) {
                    var projectName = envPro.FullName;
                    envPro.Save(null);
                    dte.Solution.Remove(envPro);
                    envPro = dte.Solution.AddFromFile(projectName, false);
                    dte = envPro.DTE;
                    vcPro = envPro.Object as VCProject;
                }
            } catch {
                Messages.DisplayErrorMessage(SR.GetString("CannotChangeQtVersion"));
                return false;
            }

            // We have to delete the generated files because of
            // major differences between the platforms or Qt-Versions.
            if (vsPlatformNameOld != vsPlatformNameNew || viOld.qtPatch != viNew.qtPatch
                || viOld.qtMinor != viNew.qtMinor || viOld.qtMajor != viNew.qtMajor) {
                DeleteGeneratedFiles();
                Clean();
            }

            if (bRefreshMocSteps)
                RefreshMocSteps();

            SetQtEnvironment(newVersion);
            UpdateModules(viOld, viNew);
            versionManager.SaveProjectQtVersion(envPro, newVersion, vsPlatformNameNew);
            return true;
        }

        public bool HasPlatform(string platformName)
        {
            foreach (VCConfiguration config in (IVCCollection) vcPro.Configurations) {
                var platform = (VCPlatform) config.Platform;
                if (platform.Name == platformName)
                    return true;
            }
            return false;
        }

        public bool SelectSolutionPlatform(string platformName)
        {
            foreach (SolutionConfiguration solutionCfg in dte.Solution.SolutionBuild.SolutionConfigurations) {
                var contexts = solutionCfg.SolutionContexts;
                for (var i = 1; i <= contexts.Count; ++i) {
                    SolutionContext ctx = null;
                    try {
                        ctx = contexts.Item(i);
                    } catch (ArgumentException) {
                        // This may happen if we encounter an unloaded project.
                        continue;
                    }

                    if (ctx.PlatformName == platformName
                        && solutionCfg.Name == dte.Solution.SolutionBuild.ActiveConfiguration.Name) {
                        solutionCfg.Activate();
                        return true;
                    }
                }
            }

            return false;
        }

        public void CreatePlatform(string oldPlatform, string newPlatform,
                                   VersionInformation viOld, VersionInformation viNew, ref bool newProjectCreated)
        {
            try {
                var cfgMgr = envPro.ConfigurationManager;
                cfgMgr.AddPlatform(newPlatform, oldPlatform, true);
                vcPro.AddPlatform(newPlatform);
                newProjectCreated = false;
            } catch {
                // That stupid ConfigurationManager can't handle platform names
                // containing dots (e.g. "Windows Mobile 5.0 Pocket PC SDK (ARMV4I)")
                // So we have to do it the nasty way...
                var projectFileName = envPro.FullName;
                envPro.Save(null);
                dte.Solution.Remove(envPro);
                AddPlatformToVCProj(projectFileName, oldPlatform, newPlatform);
                envPro = dte.Solution.AddFromFile(projectFileName, false);
                vcPro = (VCProject) envPro.Object;
                newProjectCreated = true;
            }

            // update the platform settings
            foreach (VCConfiguration config in (IVCCollection) vcPro.Configurations) {
                var vcplatform = (VCPlatform) config.Platform;
                if (vcplatform.Name == newPlatform) {
                    if (viOld != null)
                        RemovePlatformDependencies(config, viOld);
                    SetupConfiguration(config, viNew);
                }
            }

            SelectSolutionPlatform(newPlatform);
        }

        public static void RemovePlatformDependencies(VCConfiguration config, VersionInformation viOld)
        {
            var compiler = CompilerToolWrapper.Create(config);
            var minuend = new HashSet<string>(compiler.PreprocessorDefinitions);
            minuend.ExceptWith(viOld.GetQMakeConfEntry("DEFINES").Split(' ', '\t'));
            compiler.SetPreprocessorDefinitions(string.Join(",", minuend));
        }

        public void SetupConfiguration(VCConfiguration config, VersionInformation viNew)
        {
            var compiler = CompilerToolWrapper.Create(config);
            var ppdefs = new HashSet<string>(compiler.PreprocessorDefinitions);
            ppdefs.UnionWith(viNew.GetQMakeConfEntry("DEFINES").Split(' ', '\t'));
            compiler.SetPreprocessorDefinitions(string.Join(",", ppdefs));

            var linker = (VCLinkerTool) ((IVCCollection) config.Tools).Item("VCLinkerTool");
            if (linker == null)
                return;

            linker.SubSystem = subSystemOption.subSystemWindows;
            SetTargetMachine(linker, viNew);
        }

        private void DeleteGeneratedFiles()
        {
            var genFilter = Filters.GeneratedFiles();
            var genVCFilter = FindFilterFromGuid(genFilter.UniqueIdentifier);
            if (genVCFilter == null)
                return;

            var error = false;
            error = DeleteFilesFromFilter(genVCFilter);
            if (error)
                Messages.PaneMessage(dte, SR.GetString("DeleteGeneratedFilesError"));
        }

        private bool DeleteFilesFromFilter(VCFilter filter)
        {
            var error = false;
            foreach (VCFile f in filter.Files as IVCCollection) {
                try {
                    var fi = new FileInfo(f.FullPath);
                    if (fi.Exists && fi.Extension != ".cbt")
                        fi.Delete();
                    HelperFunctions.DeleteEmptyParentDirs(fi.Directory.ToString());
                } catch {
                    error = true;
                }
            }
            foreach (VCFilter filt in filter.Filters as IVCCollection)
                error |= DeleteFilesFromFilter(filt);
            return error;
        }

        public void RemoveGeneratedFiles(string fileName)
        {
            var fi = new FileInfo(fileName);
            var lastIndex = fileName.LastIndexOf(fi.Extension, StringComparison.Ordinal);
            var baseName = fi.Name.Remove(lastIndex, fi.Extension.Length);
            string delName = null;
            if (HelperFunctions.IsHeaderFile(fileName))
                delName = "moc_" + baseName + ".cpp";
            else if (HelperFunctions.IsSourceFile(fileName) && !fileName.StartsWith("moc_", StringComparison.OrdinalIgnoreCase))
                delName = baseName + ".moc";
            else if (HelperFunctions.IsUicFile(fileName))
                delName = "ui_" + baseName + ".h";
            else if (HelperFunctions.IsQrcFile(fileName))
                delName = "qrc_" + baseName + ".cpp";

            if (delName != null) {
                foreach (var delFile in GetFilesFromProject(delName))
                    RemoveFileFromFilter(delFile, Filters.GeneratedFiles());
            }
        }

        public void RemoveResFilesFromGeneratedFilesFilter()
        {
            var generatedFiles = FindFilterFromGuid(Filters.GeneratedFiles().UniqueIdentifier);
            if (generatedFiles == null)
                return;

            var filesToRemove = new List<VCFile>();
            foreach (VCFile filtFile in (IVCCollection) generatedFiles.Files) {
                if (filtFile.FullPath.EndsWith(".res", StringComparison.OrdinalIgnoreCase))
                    filesToRemove.Add(filtFile);
            }
            foreach (var resFile in filesToRemove)
                resFile.Remove();
        }

        static private void AddPlatformToVCProj(string projectFileName, string oldPlatformName, string newPlatformName)
        {
            var tempFileName = Path.GetTempFileName();
            var fi = new FileInfo(projectFileName);
            fi.CopyTo(tempFileName, true);

            var myXmlDocument = new XmlDocument();
            myXmlDocument.Load(tempFileName);
            AddPlatformToVCProj(myXmlDocument, oldPlatformName, newPlatformName);
            myXmlDocument.Save(projectFileName);

            fi = new FileInfo(tempFileName);
            fi.Delete();
        }

        static private void AddPlatformToVCProj(XmlDocument doc, string oldPlatformName, string newPlatformName)
        {
            var vsProj = doc.DocumentElement.SelectSingleNode("/VisualStudioProject");
            var platforms = vsProj.SelectSingleNode("Platforms");
            if (platforms == null) {
                platforms = doc.CreateElement("Platforms");
                vsProj.AppendChild(platforms);
            }
            var platform = platforms.SelectSingleNode("Platform[@Name='" + newPlatformName + "']");
            if (platform == null) {
                platform = doc.CreateElement("Platform");
                ((XmlElement) platform).SetAttribute("Name", newPlatformName);
                platforms.AppendChild(platform);
            }

            var configurations = vsProj.SelectSingleNode("Configurations");
            var cfgList = configurations.SelectNodes("Configuration[@Name='Debug|" + oldPlatformName + "'] | " +
                                                             "Configuration[@Name='Release|" + oldPlatformName + "']");
            foreach (XmlNode oldCfg in cfgList) {
                var newCfg = (XmlElement) oldCfg.Clone();
                newCfg.SetAttribute("Name", oldCfg.Attributes["Name"].Value.Replace(oldPlatformName, newPlatformName));
                configurations.AppendChild(newCfg);
            }

            var fileCfgPath = "Files/Filter/File/FileConfiguration";
            var fileCfgList = vsProj.SelectNodes(fileCfgPath + "[@Name='Debug|" + oldPlatformName + "'] | " +
                                                         fileCfgPath + "[@Name='Release|" + oldPlatformName + "']");
            foreach (XmlNode oldCfg in fileCfgList) {
                var newCfg = (XmlElement) oldCfg.Clone();
                newCfg.SetAttribute("Name", oldCfg.Attributes["Name"].Value.Replace(oldPlatformName, newPlatformName));
                oldCfg.ParentNode.AppendChild(newCfg);
            }
        }

        static private void SetTargetMachine(VCLinkerTool linker, VersionInformation versionInfo)
        {
            var qMakeLFlagsWindows = versionInfo.GetQMakeConfEntry("QMAKE_LFLAGS_WINDOWS");
            var rex = new Regex("/MACHINE:(\\S+)");
            var match = rex.Match(qMakeLFlagsWindows);
            if (match.Success) {
                linker.TargetMachine = HelperFunctions.TranslateMachineType(match.Groups[1].Value);
            } else {
                var platformName = versionInfo.GetVSPlatformName();
                if (platformName == "Win32")
                    linker.TargetMachine = machineTypeOption.machineX86;
                else if (platformName == "x64")
                    linker.TargetMachine = machineTypeOption.machineAMD64;
                else
                    linker.TargetMachine = machineTypeOption.machineNotSet;
            }

            var subsystemOption = string.Empty;
            var linkerOptions = linker.AdditionalOptions ?? string.Empty;

            rex = new Regex("(/SUBSYSTEM:\\S+)");
            match = rex.Match(qMakeLFlagsWindows);
            if (match.Success)
                subsystemOption = match.Groups[1].Value;

            match = rex.Match(linkerOptions);
            if (match.Success) {
                linkerOptions = rex.Replace(linkerOptions, subsystemOption);
            } else {
                if (linkerOptions.Length > 0)
                    linkerOptions += " ";
                linkerOptions += subsystemOption;
            }
            linker.AdditionalOptions = linkerOptions;
        }

        public void CollapseFilter(string filterName)
        {
            var solutionExplorer = (UIHierarchy) dte.Windows.Item(Constants.vsext_wk_SProjectWindow).Object;
            if (solutionExplorer.UIHierarchyItems.Count == 0)
                return;

            dte.SuppressUI = true;
            var projectItem = FindProjectHierarchyItem(solutionExplorer);
            if (projectItem != null)
                HelperFunctions.CollapseFilter(projectItem, solutionExplorer, filterName);
            dte.SuppressUI = false;
        }

        private UIHierarchyItem FindProjectHierarchyItem(UIHierarchy hierarchy)
        {
            if (hierarchy.UIHierarchyItems.Count == 0)
                return null;

            var solution = hierarchy.UIHierarchyItems.Item(1);
            UIHierarchyItem projectItem = null;
            foreach (UIHierarchyItem solutionItem in solution.UIHierarchyItems) {
                projectItem = FindProjectHierarchyItem(solutionItem);
                if (projectItem != null)
                    break;
            }
            return projectItem;
        }

        private UIHierarchyItem FindProjectHierarchyItem(UIHierarchyItem root)
        {
            UIHierarchyItem projectItem = null;
            try {
                if (root.Name == envPro.Name)
                    return root;

                foreach (UIHierarchyItem childItem in root.UIHierarchyItems) {
                    projectItem = FindProjectHierarchyItem(childItem);
                    if (projectItem != null)
                        break;
                }
            } catch {
            }
            return projectItem;
        }

        /// <summary>
        /// Gets the Qt version of the project
        /// </summary>
        public string GetQtVersion()
        {
            return QtVersionManager.The().GetProjectQtVersion(envPro);
        }

        /// <summary>
        /// Sets the Qt environment for the project's Qt version.
        /// </summary>
        public void SetQtEnvironment()
        {
            SetQtEnvironment(QtVersionManager.The().GetProjectQtVersion(envPro));
        }

        /// <summary>
        /// Sets the Qt environment for the given Qt version.
        /// </summary>
        public void SetQtEnvironment(string qtVersion)
        {
            SetQtEnvironment(qtVersion, string.Empty);
        }

        /// <summary>
        /// Sets the Qt environment for the given Qt version.
        /// </summary>
        public void SetQtEnvironment(string qtVersion, string solutionConfig, bool build = false)
        {
            if (string.IsNullOrEmpty(qtVersion))
                return;

            string qtDir = null;
            if (qtVersion != "$(QTDIR)")
                qtDir = QtVersionManager.The().GetInstallPath(qtVersion);
            HelperFunctions.SetEnvironmentVariableEx("QTDIR", qtDir);
            try {
                var propertyAccess = (IVCBuildPropertyStorage) vcPro;
                var vcprj = envPro.Object as VCProject;

                // Get platform name from given solution configuration
                // or if not available take the active configuration
                var activePlatformName = string.Empty;
                if (string.IsNullOrEmpty(solutionConfig)) {
                    // First get active configuration cause not given as parameter
                    var activeConf = envPro.ConfigurationManager.ActiveConfiguration;
                    solutionConfig = activeConf.ConfigurationName + "|" + activeConf.PlatformName;
                    activePlatformName = activeConf.PlatformName;
                } else {
                    activePlatformName = solutionConfig.Split('|')[1];
                }

                // Find all configurations for platform and set property for all of them
                // This is to get QTDIR property set for all configurations same time so
                // we can be sure it is set and is equal between debug and release
                foreach (VCConfiguration conf in vcprj.Configurations as IVCCollection) {
                    var cur_platform = conf.Platform as VCPlatform;
                    if (cur_platform.Name == activePlatformName) {
                        var cur_solution = conf.ConfigurationName + "|" + cur_platform.Name;
                        // If the LocalDebuggerEnvironment property is defined, it
                        // will be stored in the .user file before the QTDIR property, which is an
                        // error because there is a dependency. To work around this, first remove
                        // the property and then add it after QTDIR is defined.
                        var debuggerEnv = string.Empty;
                        if (!build) {
                            debuggerEnv = propertyAccess.GetPropertyValue(
                                "LocalDebuggerEnvironment", cur_solution, "UserFile");
                            if (!string.IsNullOrEmpty(debuggerEnv)) {
                                var debugSettings = conf.DebugSettings as VCDebugSettings;
                                if (debugSettings != null) {
                                    //Get original value without expanded properties
                                    debuggerEnv = debugSettings.Environment;
                                }
                                propertyAccess.RemoveProperty(
                                    "LocalDebuggerEnvironment", cur_solution, "UserFile");
                            }
                        }
                        propertyAccess.SetPropertyValue("QTDIR", cur_solution, "UserFile", qtDir);
                        if (!string.IsNullOrEmpty(debuggerEnv))
                            propertyAccess.SetPropertyValue(
                                "LocalDebuggerEnvironment", cur_solution, "UserFile", debuggerEnv);
                    }
                }

            } catch (Exception) {
                Messages.PaneMessage(envPro.DTE, SR.GetString("QtProject_CannotAccessUserFile", vcPro.ItemName));
            }

            HelperFunctions.SetDebuggingEnvironment(envPro);
        }
    }

    public class VCPropertyStorageProvider : IPropertyStorageProvider
    {
        string GetProperty(IVCRulePropertyStorage propertyStorage, string propertyName)
        {
            if (propertyStorage == null)
                return "";
            return propertyStorage.GetUnevaluatedPropertyValue(propertyName);
        }

        public string GetProperty(object propertyStorage, string itemType, string propertyName)
        {
            if (propertyStorage == null)
                return "";
            if (propertyStorage is VCFileConfiguration)
                return GetProperty(
                    (propertyStorage as VCFileConfiguration).Tool
                    as IVCRulePropertyStorage,
                    propertyName);
            else if (propertyStorage is VCConfiguration)
                return GetProperty(
                    (propertyStorage as VCConfiguration).Rules.Item(itemType)
                    as IVCRulePropertyStorage,
                    propertyName);
            return "";
        }

        static bool SetProperty(
            IVCRulePropertyStorage propertyStorage,
            string propertyName,
            string propertyValue)
        {
            if (propertyStorage == null)
                return false;
            if (propertyStorage.GetUnevaluatedPropertyValue(propertyName) != propertyValue)
                propertyStorage.SetPropertyValue(propertyName, propertyValue);
            return true;
        }

        public bool SetProperty(
            object propertyStorage,
            string itemType,
            string propertyName,
            string propertyValue)
        {
            if (propertyStorage == null)
                return false;
            if (propertyStorage is VCFileConfiguration)
                return SetProperty(
                    (propertyStorage as VCFileConfiguration).Tool
                    as IVCRulePropertyStorage,
                    propertyName,
                    propertyValue);
            else if (propertyStorage is VCConfiguration)
                return SetProperty(
                    (propertyStorage as VCConfiguration).Rules.Item(itemType)
                    as IVCRulePropertyStorage,
                    propertyName,
                    propertyValue);
            return false;
        }

        static bool DeleteProperty(IVCRulePropertyStorage propertyStorage, string propertyName)
        {
            if (propertyStorage == null)
                return false;
            propertyStorage.DeleteProperty(propertyName);
            return true;
        }

        public bool DeleteProperty(object propertyStorage, string itemType, string propertyName)
        {
            if (propertyStorage == null)
                return false;
            if (propertyStorage is VCFileConfiguration)
                return DeleteProperty(
                    (propertyStorage as VCFileConfiguration).Tool
                    as IVCRulePropertyStorage,
                    propertyName);
            else if (propertyStorage is VCConfiguration)
                return DeleteProperty(
                    (propertyStorage as VCConfiguration).Rules.Item(itemType)
                    as IVCRulePropertyStorage,
                    propertyName);
            return false;
        }

        public string GetConfigName(object propertyStorage)
        {
            if (propertyStorage == null)
                return "";
            if (propertyStorage is VCFileConfiguration)
                return (propertyStorage as VCFileConfiguration).Name;
            else if (propertyStorage is VCConfiguration)
                return (propertyStorage as VCConfiguration).Name;
            return "";
        }

        string GetItemType(VCFileConfiguration propertyStorage)
        {
            if (propertyStorage == null)
                return "";
            VCFile file = propertyStorage.File as VCFile;
            if (file == null)
                return "";
            return file.ItemType;
        }

        public string GetItemType(object propertyStorage)
        {
            if (propertyStorage == null)
                return "";
            if (propertyStorage is VCFileConfiguration)
                return GetItemType(propertyStorage as VCFileConfiguration);
            return "";
        }

        string GetItemName(VCFileConfiguration propertyStorage)
        {
            if (propertyStorage == null)
                return "";
            VCFile file = propertyStorage.File as VCFile;
            if (file == null)
                return "";
            return file.Name;
        }

        public string GetItemName(object propertyStorage)
        {
            if (propertyStorage == null)
                return "";
            if (propertyStorage is VCFileConfiguration)
                return GetItemName(propertyStorage as VCFileConfiguration);
            return "";
        }

        object GetParentProject(VCConfiguration propertyStorage)
        {
            if (propertyStorage == null)
                return null;
            return propertyStorage.project as VCProject;
        }

        object GetParentProject(VCFileConfiguration propertyStorage)
        {
            if (propertyStorage == null)
                return null;
            return GetParentProject(propertyStorage.ProjectConfiguration as VCConfiguration);
        }

        public object GetParentProject(object propertyStorage)
        {
            if (propertyStorage == null)
                return null;
            if (propertyStorage is VCFileConfiguration)
                return GetParentProject(propertyStorage as VCFileConfiguration);
            else if (propertyStorage is VCConfiguration)
                return GetParentProject(propertyStorage as VCConfiguration);
            return null;
        }

        object GetProjectConfiguration(VCProject project, string configName)
        {
            if (project == null)
                return null;
            foreach (VCConfiguration projConfig in (IVCCollection)project.Configurations) {
                if (projConfig.Name == configName)
                    return projConfig;
            }
            return null;
        }

        public object GetProjectConfiguration(object project, string configName)
        {
            if (project == null)
                return null;
            return GetProjectConfiguration(project as VCProject, configName);
        }

        IEnumerable<object> GetItems(VCProject project, string itemType, string configName = "")
        {
            if (project == null)
                return new List<object>();
            var allItems = project.GetFilesWithItemType(itemType) as IVCCollection;
            var items = new List<VCFileConfiguration>();
            foreach (VCFile vcFile in allItems) {
                foreach (VCFileConfiguration vcFileConfig
                    in vcFile.FileConfigurations as IVCCollection) {
                    if (!string.IsNullOrEmpty(configName) && vcFileConfig.Name != configName)
                        continue;
                    items.Add(vcFileConfig);
                }
            }
            return items;
        }

        public IEnumerable<object> GetItems(
            object project,
            string itemType,
            string configName = "")
        {
            if (project == null)
                return null;
            return GetItems(project as VCProject, itemType, configName);
        }

    }

    public class VCMacroExpander : IVSMacroExpander
    {
        object config;

        public VCMacroExpander(object config)
        {
            this.config = config;
        }

        public string ExpandString(string stringToExpand)
        {
            HelperFunctions.ExpandString(ref stringToExpand, config);
            return stringToExpand;
        }
    }

    public class QtCustomBuildTool
    {
        QtMsBuildContainer qtMsBuild;
        VCFileConfiguration vcConfig;
        VCFile vcFile;
        VCCustomBuildTool tool;
        VCMacroExpander macros;

        enum FileItemType { Other = 0, CustomBuild, QtMoc, QtRcc, QtUic };
        FileItemType itemType = FileItemType.Other;
        public QtCustomBuildTool(VCFileConfiguration vcConfig, QtMsBuildContainer container = null)
        {
            if (container != null)
                qtMsBuild = container;
            else
                qtMsBuild = new QtMsBuildContainer(new VCPropertyStorageProvider());
            this.vcConfig = vcConfig;
            if (vcConfig != null)
                vcFile = vcConfig.File as VCFile;
            if (vcFile != null) {
                if (vcFile.ItemType == "CustomBuild")
                    itemType = FileItemType.CustomBuild;
                else if (vcFile.ItemType == QtMoc.ItemTypeName)
                    itemType = FileItemType.QtMoc;
                else if (vcFile.ItemType == QtRcc.ItemTypeName)
                    itemType = FileItemType.QtRcc;
                else if (vcFile.ItemType == QtUic.ItemTypeName)
                    itemType = FileItemType.QtUic;
            }
            if (itemType == FileItemType.CustomBuild)
                tool = HelperFunctions.GetCustomBuildTool(vcConfig);
            macros = new VCMacroExpander(vcConfig);
        }

        public string CommandLine
        {
            get
            {
                switch (itemType) {
                    case FileItemType.CustomBuild:
                        return (tool != null) ? tool.CommandLine : "";
                    case FileItemType.QtMoc:
                        return qtMsBuild.GenerateQtMocCommandLine(vcConfig);
                    case FileItemType.QtRcc:
                        return qtMsBuild.GenerateQtRccCommandLine(vcConfig);
                    case FileItemType.QtUic:
                        return qtMsBuild.GenerateQtUicCommandLine(vcConfig);
                }
                return "";
            }
            set
            {
                switch (itemType) {
                    case FileItemType.CustomBuild:
                        if (tool != null)
                            tool.CommandLine = value;
                        break;
                    case FileItemType.QtMoc:
                        qtMsBuild.SetQtMocCommandLine(vcConfig, value, macros);
                        break;
                    case FileItemType.QtRcc:
                        qtMsBuild.SetQtRccCommandLine(vcConfig, value, macros);
                        break;
                    case FileItemType.QtUic:
                        qtMsBuild.SetQtUicCommandLine(vcConfig, value, macros);
                        break;
                }
            }
        }

        public string Outputs
        {
            get
            {
                switch (itemType) {
                    case FileItemType.CustomBuild:
                        return (tool != null) ? tool.Outputs : "";
                    case FileItemType.QtMoc:
                        return qtMsBuild.GetPropertyValue(vcConfig, QtMoc.Property.OutputFile);
                    case FileItemType.QtRcc:
                        return qtMsBuild.GetPropertyValue(vcConfig, QtRcc.Property.OutputFile);
                    case FileItemType.QtUic:
                        return qtMsBuild.GetPropertyValue(vcConfig, QtUic.Property.OutputFile);
                }
                return "";
            }
        }

    }

}
