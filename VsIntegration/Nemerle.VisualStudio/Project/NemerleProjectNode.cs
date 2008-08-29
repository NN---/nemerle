using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Package;
using Microsoft.VisualStudio.Package.Automation;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Windows.Design.Host;

using Nemerle.VisualStudio.LanguageService;
using Nemerle.VisualStudio.Project.PropertyPages;
using Nemerle.VisualStudio.WPFProviders;

using PkgUtils = Microsoft.VisualStudio.Package.Utilities;
using MSBuild = Microsoft.Build.BuildEngine;

namespace Nemerle.VisualStudio.Project
{
	internal enum GeneralPropertyPageTag
	{
		AssemblyName,
		OutputType,
		RootNamespace,
		StartupObject,
		ApplicationIcon,
		TargetPlatform,
		TargetPlatformLocation
	}

	[ComVisible(true)]
	[CLSCompliant(false)]
	[ClassInterface(ClassInterfaceType.AutoDual)]
	[Guid(NemerleConstants.ProjectNodeGuidString)]
	public class NemerleProjectNode : ProjectNode, IVsProjectSpecificEditorMap2
	{
		#region Init

		public NemerleProjectNode(NemerlePackage pkg)
		{
			FileTemplateProcessor   = new NemerleTokenProcessor();
			CanFileNodesHaveChilds  = true;
			SupportsProjectDesigner = true;

			OleServiceProvider.AddService(typeof(VSLangProj.VSProject), VSProject, false);

			// Store the number of images in ProjectNode so we know the offset of the Nemerle icons.
			//
			_imageOffset = ImageHandler.ImageList.Images.Count;

			foreach (Image img in NemerleImageList.Images)
			{
				ImageHandler.ImageList.Images.Add(img);
			}

			InitializeCATIDs();

			CanProjectDeleteItems = true;
		}

		/// <summary>
		/// Provide mapping from our browse objects and automation objects to our CATIDs
		/// </summary>
		void InitializeCATIDs()
		{
			// The following properties classes are specific to Nemerle so we can use their GUIDs directly
			//
			AddCATIDMapping(typeof(NemerleProjectNodeProperties), typeof(NemerleProjectNodeProperties).GUID);
			AddCATIDMapping(typeof(NemerleFileNodeProperties),    typeof(NemerleFileNodeProperties).GUID);
			AddCATIDMapping(typeof(NemerleOAFileItem),            typeof(NemerleOAFileItem).GUID);

			// The following are not specific to Nemerle and as such we need a separate GUID
			// (we simply used guidgen.exe to create new guids)
			//
			AddCATIDMapping(typeof(FolderNodeProperties), new Guid(NemerleConstants.FolderNodePropertiesGuidString));

			// This one we use the same as Nemerle file nodes since both refer to files
			//
			AddCATIDMapping(typeof(FileNodeProperties), typeof(NemerleFileNodeProperties).GUID);

			// Because our property page pass itself as the object to display in its grid,
			// we need to make it have the same CATID as the browse object of the project node
			// so that filtering is possible.
			//
			AddCATIDMapping(typeof(NemerleGeneralPropertyPage), typeof(NemerleProjectNodeProperties).GUID);

			// We could also provide CATIDs for references and the references container node, if we wanted to.
		}

		private static ImageList LoadProjectImageList()
		{
			// Make the name of resource bitmap.
			// It's bitmap used in project ImageList.
			//
			Type	 type		 = typeof(NemerleProjectNode);
			Assembly assembly	 = type.Assembly;
			Stream   imageStream = assembly.GetManifestResourceStream(
				NemerleConstants.ProjectImageListName);

			Debug.Assert(imageStream != null);

			return PkgUtils.GetImageList(imageStream);
		}

		#endregion

		#region Properties

		public new NemerlePackage Package
		{
			get { return (NemerlePackage)base.Package; }
		}

		public string OutputFileName
		{
			get
			{
				string assemblyName =
					ProjectMgr.GetProjectProperty(
						GeneralPropertyPageTag.AssemblyName.ToString(), true);

				string outputTypeAsString =
					ProjectMgr.GetProjectProperty(
						GeneralPropertyPageTag.OutputType.ToString(), false);

				OutputType outputType =
					(OutputType)Enum.Parse(typeof (OutputType), outputTypeAsString);

				return assemblyName + GetOutputExtension(outputType);
			}
		}

		private			VSLangProj.VSProject _vsProject;
		protected internal VSLangProj.VSProject  VSProject
		{
			get
			{
				if (_vsProject == null)
					_vsProject = new OAVSProject(this);
				return _vsProject;
			}
		}

		IVsHierarchy InteropSafeHierarchy
		{
			get
			{
				IntPtr unknownPtr = PkgUtils.QueryInterfaceIUnknown(this);

				if (unknownPtr == IntPtr.Zero)
					return null;

				return (IVsHierarchy)Marshal.GetObjectForIUnknown(unknownPtr);
			}
		}

		private ProjectInfo _projectInfo;
		public  ProjectInfo  ProjectInfo
		{
			get { return _projectInfo;  }
		}

		private static ImageList _nemerleImageList = LoadProjectImageList();
		
		static int _imageOffset;

		public  static ImageList  NemerleImageList
		{
			get { return _nemerleImageList; }
		}

		private            DesignerContext _designerContext;
		protected internal DesignerContext  DesignerContext
		{
			get
			{
				//Set the RuntimeNameProvider so the XAML designer will call it when items are added to
				//a design surface. Since the provider does not depend on an item context, we provide it at 
				//the project level.
				return _designerContext ??
					(_designerContext = new DesignerContext() { RuntimeNameProvider = new NemerleRuntimeNameProvider() });
			}
		}

		#endregion

		#region Overridden Properties

		public   override int	ImageIndex  { get { return _imageOffset + NemerleConstants.ImageListIndex.NemerleProject; } }
		public   override Guid   ProjectGuid { get { return typeof(NemerleProjectFactory).GUID; } }
		public   override string ProjectType { get { return NemerleConstants.LanguageName;	  } }
		internal override object Object	  { get { return VSProject;						  } }

		protected override ReferenceContainerNode CreateReferenceContainerNode()
		{
			return new NemerleReferenceContainerNode(this);
		}

		#endregion

		#region Overridden Methods

		protected internal override void ProcessFolders()
		{
			// Process Folders (useful to persist empty folder)
			var folders = BuildProject.GetEvaluatedItemsByName(ProjectFileConstants.Folder);
			foreach (MSBuild.BuildItem folder in folders)
			{
				string strPath = folder.FinalItemSpec;

				// We do not need any special logic for assuring that a folder is only added once to the ui hierarchy.
				// The below method will only add once the folder to the ui hierarchy
				this.CreateFolderNodes(strPath);
			}
		}

		/// <summary>
		/// Walks the subpaths of a project relative path and checks if the folder nodes 
		/// hierarchy is already there, if not creates it.
		/// </summary>
		/// <param name="strPath">Path of the folder, can be relative to project or absolute</param>
		public override HierarchyNode CreateFolderNodes(string path)
		{
			if (String.IsNullOrEmpty(path))
			{
				throw new ArgumentNullException("path");
			}

			if (Path.IsPathRooted(path))
			{
				// Ensure we are using a relative path
				if (String.Compare(ProjectFolder, 0, path, 0, ProjectFolder.Length, StringComparison.OrdinalIgnoreCase) == 0)
					path = path.Substring(ProjectFolder.Length);
				else
				{
					Debug.Assert(false, "Folder path is rooted, but not subpath of ProjectFolder.");
				}
			}

			string[] parts;
			HierarchyNode curParent;

			parts = path.Split(Path.DirectorySeparatorChar);
			path = String.Empty;
			curParent = this;

			// now we have an array of subparts....
			for (int i = 0; i < parts.Length; i++)
			{
				if (parts[i].Length > 0)
				{
					path += parts[i] + "\\";
					curParent = VerifySubFolderExists(path, curParent);
				}
			}

			return curParent;
		}

		/// <summary>
		/// Loads file items from the project file into the hierarchy.
		/// </summary>
		protected internal override void ProcessFiles()
		{
			List<String> subitemsKeys = new List<String>();
			var subitems = new Dictionary<String, MSBuild.BuildItem>();

			// Define a set for our build items. The value does not really matter here.
			var items = new Dictionary<String, MSBuild.BuildItem>();

			// Process Files
			var projectFiles = BuildProject.EvaluatedItems;

			foreach (MSBuild.BuildItem item in projectFiles)
			{
				// Ignore the item if it is a reference or folder
				if (this.FilterItemTypeToBeAddedToHierarchy(item.Name))
					continue;

				// MSBuilds tasks/targets can create items (such as object files),
				// such items are not part of the project per say, and should not be displayed.
				// so ignore those items.
				if (!this.IsItemTypeFileType(item.Name))
					continue;

				// If the item is already contained do nothing.
				// TODO: possibly report in the error list that the the item is already contained in the project file similar to Language projects.
				if (items.ContainsKey(item.FinalItemSpec.ToUpperInvariant()))
					continue;

				// Make sure that we do not want to add the item, dependent, or independent twice to the ui hierarchy
				items.Add(item.FinalItemSpec.ToUpperInvariant(), item);

				string dependentOf = item.GetMetadata(ProjectFileConstants.DependentUpon);

				if (!this.CanFileNodesHaveChilds || String.IsNullOrEmpty(dependentOf))
					AddIndependentFileNode(item);
				else
				{
					// We will process dependent items later.
					// Note that we use 2 lists as we want to remove elements from
					// the collection as we loop through it
					subitemsKeys.Add(item.FinalItemSpec);
					subitems.Add(item.FinalItemSpec, item);
				}
			}

			// Now process the dependent items.
			if (this.CanFileNodesHaveChilds)
				ProcessDependentFileNodes(subitemsKeys, subitems);
		}

		/// <summary>
		/// Add an item to the hierarchy based on the item path
		/// </summary>
		/// <param name="item">Item to add</param>
		/// <returns>Added node</returns>
		private HierarchyNode AddIndependentFileNode(MSBuild.BuildItem item)
		{
			return AddFileNodeToNode(item, GetItemParentNode(item));
		}

		/// <summary>
		/// Add a file node to the hierarchy
		/// </summary>
		/// <param name="item">msbuild item to add</param>
		/// <param name="parentNode">Parent Node</param>
		/// <returns>Added node</returns>
		private HierarchyNode AddFileNodeToNode(MSBuild.BuildItem item, HierarchyNode parentNode)
		{
			FileNode node = this.CreateFileNode(new ProjectElement(this, item, false));
			parentNode.AddChild(node);
			return node;
		}

		static bool IsLinkNode(MSBuild.BuildItem item)
		{
			return item.CustomMetadataNames.Cast<string>().Contains("Link");
		}

		/// <summary>
		/// Get the parent node of an msbuild item
		/// </summary>
		/// <param name="item">msbuild item</param>
		/// <returns>parent node</returns>
		private HierarchyNode GetItemParentNode(MSBuild.BuildItem item)
		{
			var isLink = IsLinkNode(item);
			var path = isLink ? item.GetMetadata("Link") : item.FinalItemSpec;
			var dir = Path.GetDirectoryName(path);
			return Path.IsPathRooted(dir) || string.IsNullOrEmpty(dir)
				? this : CreateFolderNodes(dir);
		}

		protected override MSBuildResult InvokeMsBuild(string target)
		{
			((NemerleIdeBuildLogger)BuildLogger).BuildTargetName = target;
			return base.InvokeMsBuild(target);
		}

		NemerleOAProject _automationObject;
		/// <summary>
		/// Gets the automation object for the project node.
		/// </summary>
		/// <returns>An instance of an EnvDTE.Project implementation object representing the automation object for the project.</returns>
		public override object GetAutomationObject()
		{
			if (_automationObject == null)
				_automationObject = new NemerleOAProject(this);
			return _automationObject;
		}

		protected override void SetOutputLogger(IVsOutputWindowPane output)
		{
			//base.SetOutputLogger(output);
			// Create our logger, if it was not specified
			if (BuildLogger == null)
			{
				// Because we may be aggregated, we need to make sure to get the outer IVsHierarchy
				IntPtr unknown = IntPtr.Zero;
				IVsHierarchy hierarchy = null;
				try
				{
					unknown = Marshal.GetIUnknownForObject(this);
					hierarchy = Marshal.GetTypedObjectForIUnknown(unknown, typeof(IVsHierarchy)) as IVsHierarchy;
				}
				finally
				{
					if (unknown != IntPtr.Zero)
						Marshal.Release(unknown);
				}
				// Create the logger
				BuildLogger = new NemerleIdeBuildLogger(output, this.TaskProvider, hierarchy);

				// To retrive the verbosity level, the build logger depends on the registry root 
				// (otherwise it will used an hardcoded default)
				ILocalRegistry2 registry = this.GetService(typeof(SLocalRegistry)) as ILocalRegistry2;
				if (null != registry)
				{
					string registryRoot;
					registry.GetLocalRegistryRoot(out registryRoot);
					IDEBuildLogger logger = this.BuildLogger as IDEBuildLogger;
					if (!String.IsNullOrEmpty(registryRoot) && (null != logger))
					{
						logger.BuildVerbosityRegistryRoot = registryRoot;
						logger.ErrorString = this.ErrorString;
						logger.WarningString = this.WarningString;
					}
				}
			}
			else
			{
				((NemerleIdeBuildLogger)this.BuildLogger).OutputWindowPane = output;
			}

			if (BuildEngine != null)
			{
				BuildEngine.UnregisterAllLoggers();
				BuildEngine.RegisterLogger(BuildLogger);
			}
		}

		bool _suppressDispose;
		protected override void Dispose(bool disposing)
		{
			if (!_suppressDispose)
				base.Dispose(disposing);
		}

		public override int Close()
		{
			if (null != Site)
			{
				INemerleLibraryManager libraryManager =
					Site.GetService(typeof(INemerleLibraryManager)) as INemerleLibraryManager;
				
				if (null != libraryManager)
					libraryManager.UnregisterHierarchy(InteropSafeHierarchy);
			}

			int result;

			// Prevent early disposing...
			_suppressDispose = true;

			try { result = base.Close(); }
			catch (COMException ex) { result = ex.ErrorCode; }
			finally
			{
				_suppressDispose = false;
				base.Dispose(true);
			}

			return result;
		}

		public override void Load(
			string   filename,
			string   location,
			string   name,
			uint	 flags,
			ref Guid iidProject,
			out int  canceled)
		{
			Debug.Assert(BuildEngine  != null);
			Debug.Assert(BuildProject != null);
			Debug.Assert(BuildProject.FullFileName == Path.GetFullPath(filename));

			// IT: ProjectInfo needs to be created before loading
			// as we will catch assembly reference adding.
			//
			_projectInfo = new ProjectInfo(this, InteropSafeHierarchy, 
				Utils.GetService<NemerleLanguageService>(Site), filename, location);

			ProjectInfo.Projects.Add(_projectInfo);

			base.Load(filename, location, name, flags, ref iidProject, out canceled);

			// WAP ask the designer service for the CodeDomProvider corresponding to the project node.
			//

			// AKhropov: now NemerleFileNode provides these services
			//OleServiceProvider.AddService(typeof(SVSMDCodeDomProvider), this.CodeDomProvider, false);
			//OleServiceProvider.AddService(typeof(CodeDomProvider), CodeDomProvider.CodeDomProvider, false);

			// IT: Initialization sequence is important.
			//
			ProjectInfo.InitListener();

			INemerleLibraryManager libraryManager = Utils.GetService<INemerleLibraryManager>(Site);

			if (libraryManager != null)
				libraryManager.RegisterHierarchy(InteropSafeHierarchy);

			// If this is a WPFFlavor-ed project, then add a project-level DesignerContext service to provide
			// event handler generation (EventBindingProvider) for the XAML designer.
			OleServiceProvider.AddService(typeof(DesignerContext), DesignerContext, false);
		}

		/// <summary>
		/// Overriding to provide project general property page
		/// </summary>
		/// <returns></returns>
		protected override Guid[] GetConfigurationIndependentPropertyPages()
		{
			return new Guid[] { typeof(NemerleGeneralPropertyPage).GUID };
		}

		/// <summary>
		/// Returns the configuration dependent property pages.
		/// Specify here a property page. By returning no property page the 
		/// configuartion dependent properties will be neglected. Overriding, but 
		/// current implementation does nothing. To provide configuration specific
		/// page project property page, this should return an array bigger then 0
		/// (you can make it do the same as 
		/// GetConfigurationIndependentPropertyPages() to see its 
		/// impact)
		/// </summary>
		/// <returns></returns>
		protected override Guid[] GetConfigurationDependentPropertyPages()
		{
			return new Guid[]
			{
				typeof(NemerleBuildPropertyPage).GUID,
				typeof(NemerleDebugPropertyPage).GUID,
			};
		}

		/// <summary>
		/// Overriding to provide customization of files on add files.
		/// This will replace tokens in the file with actual value (namespace, 
		/// class name,...)
		/// </summary>
		/// <param name="source">Full path to template file</param>
		/// <param name="target">Full path to destination file</param>
		public override void AddFileFromTemplate(string source, string target)
		{
			if (!File.Exists(source))
				throw new FileNotFoundException(
					String.Format("Template file not found: {0}", source));

			// We assume that there is no token inside the file because the only
			// way to add a new element should be through the template wizard that
			// take care of expanding and replacing the tokens.
			// The only task to perform is to copy the source file in the
			// target location.
			string targetFolder = Path.GetDirectoryName(target);
			if (!Directory.Exists(targetFolder))
			{
				Directory.CreateDirectory(targetFolder);
			}

			File.Copy(source, target);
		}

		// KLiss: body of this method is copy/pasted from base one (and modified),
		// as base implementation does not allow changing parent node on-the-fly.
		protected override void AddNewFileNodeToHierarchy(HierarchyNode parentNode, string fileName)
		{
			HierarchyNode child;
			HierarchyNode newParent;

			// KLiss: try to find possible parent file (ie, Form3.n would be parent for Form3.designer.n
			bool parentFound = TryFindParentFileNode(parentNode, fileName, out newParent);
			if (parentFound)
			{
				parentNode = newParent;

				// KLiss: when file is added to project, it is treated as code file,
				// regardless of SubType value, specified in the template.
				// SubType is assigned correct value later, and now we will make another 
				// attempt to find out, whether it is OK for an item to have designer, or not.
				var nemerleParent = parentNode as NemerleFileNode;
				if (nemerleParent != null)
				{
					nemerleParent.InferHasDesignerFromSubType();
				}
			}

			// In the case of subitem, we want to create dependent file node
			// and set the DependentUpon property
			if (parentFound || parentNode is FileNode || parentNode is DependentFileNode)
			{
				child = this.CreateDependentFileNode(fileName);

				child.ItemNode.SetMetadata(ProjectFileConstants.DependentUpon, parentNode.ItemNode.GetMetadata(ProjectFileConstants.Include));

				// Make sure to set the HasNameRelation flag on the dependent node if it is related to the parent by name
				if (!child.HasParentNodeNameRelation && string.Compare(child.GetRelationalName(), parentNode.GetRelationalName(), StringComparison.OrdinalIgnoreCase) == 0)
				{
					child.HasParentNodeNameRelation = true;
				}
			}
			else
			{
				//Create and add new filenode to the project
				child = this.CreateFileNode(fileName);
			}

			parentNode.AddChild(child);

			//// TODO : Revisit the VSADDFILEFLAGS here. Can it be a nested project?
			//this.tracker.OnItemAdded(fileName, VSADDFILEFLAGS.VSADDFILEFLAGS_NoFlags);
		}

		private bool TryFindParentFileNode(HierarchyNode root, string child, out HierarchyNode parent)
		{
			parent = root;
			var relationIndex = child.IndexOf(root.NameRelationSeparator);

			if (relationIndex < 0)
				return false;

			var parentName = string.Format("{0}.n", child.Substring(0, relationIndex));

			parent = root.FindChild(parentName);
			return parent != null;
		}

		
		/// <summary>
		/// Evaluates if a file is an Nemerle code file based on is extension
		/// </summary>
		/// <param name="strFileName">The filename to be evaluated</param>
		/// <returns>true if is a code file</returns>
		public override bool IsCodeFile(string strFileName)
		{
			// We do not want to assert here, just return silently.
			//
			if (string.IsNullOrEmpty(strFileName))
				return false;

			return
				string.Compare(
					Path.GetExtension(strFileName),
					NemerleConstants.FileExtension, 
					StringComparison.OrdinalIgnoreCase) == 0;
		}


		public override bool IsEmbeddedResource(string strFileName)
		{
			return base.IsEmbeddedResource(strFileName) ||
				StringComparer.OrdinalIgnoreCase.Compare(Path.GetExtension(strFileName), ".licx") == 0;
		}

		/// <summary>
		/// Create a file node based on an msbuild item.
		/// </summary>
		/// <param name="item">The msbuild item to be analyzed</param>
		/// <returns>NemerleFileNode or FileNode</returns>
		public override FileNode CreateFileNode(ProjectElement item)
		{
			if (item == null)
				throw new ArgumentNullException("item");

			NemerleFileNode newNode = new NemerleFileNode(this, item);
			string		  include = item.GetMetadata(ProjectFileConstants.Include);
			
			newNode.OleServiceProvider.AddService(typeof(EnvDTE.Project),	   ProjectMgr.GetAutomationObject(), false);
			newNode.OleServiceProvider.AddService(typeof(EnvDTE.ProjectItem), newNode.GetAutomationObject(), false);
			newNode.OleServiceProvider.AddService(typeof(VSLangProj.VSProject), this.VSProject, false);

			if (IsCodeFile(include) && item.ItemName == "Compile")
				newNode.OleServiceProvider.AddService(typeof(SVSMDCodeDomProvider),
					new VSMDCodeDomProvider( newNode.CodeDomProvider ), false);

			return newNode;
		}

		/// <summary>
		/// Create dependent file node based on an msbuild item
		/// </summary>
		/// <param name="item">msbuild item</param>
		/// <returns>dependent file node</returns>
		public override DependentFileNode CreateDependentFileNode(ProjectElement item)
		{
			if (item == null) throw new ArgumentNullException("item");

			NemerleDependentFileNode newNode = new NemerleDependentFileNode(this, item);
			string				   include = item.GetMetadata(ProjectFileConstants.Include);

			newNode.OleServiceProvider.AddService(typeof(EnvDTE.Project), ProjectMgr.GetAutomationObject(), false);
			newNode.OleServiceProvider.AddService(typeof(EnvDTE.ProjectItem), newNode.GetAutomationObject(), false);
			newNode.OleServiceProvider.AddService(typeof(VSLangProj.VSProject), this.VSProject, false);

			if (IsCodeFile(include) && item.ItemName == "Compile")
				newNode.OleServiceProvider.AddService(typeof(SVSMDCodeDomProvider),
					new VSMDCodeDomProvider(newNode.CodeDomProvider), false);

			return newNode;
		}

		/// <summary>
		/// Creates the format list for the open file dialog
		/// </summary>
		/// <param name="formatlist">The formatlist to return</param>
		/// <returns>Success</returns>
		public override int GetFormatList(out string formatlist)
		{
			formatlist = string.Format(
				CultureInfo.CurrentCulture, SR.GetString(SR.ProjectFileExtensionFilter), "\0", "\0");

			return VSConstants.S_OK;
		}

		/// <summary>
		/// This overrides the base class method to show the VS 2005 style Add 
		/// reference dialog. The ProjectNode implementation shows the VS 2003 
		/// style Add Reference dialog.
		/// </summary>
		/// <returns>S_OK if succeeded. Failure other wise</returns>
		public override int AddProjectReference()
		{
			IVsComponentSelectorDlg2     componentDialog;
			Guid                         startOnTab      = Guid.Empty;
			VSCOMPONENTSELECTORTABINIT[] tabInit         = new VSCOMPONENTSELECTORTABINIT[5];
			string                       browseLocations = Path.GetDirectoryName(BaseURI.Uri.LocalPath);
			Guid                         GUID_MruPage    = new Guid("{19B97F03-9594-4c1c-BE28-25FF030113B3}");

			// Add the .NET page.
			//
			tabInit[0].dwSize         = (uint)Marshal.SizeOf(typeof(VSCOMPONENTSELECTORTABINIT));
			tabInit[0].varTabInitInfo = 0;
			tabInit[0].guidTab        = VSConstants.GUID_COMPlusPage;

			// Add the COM page.
			//
			tabInit[1].dwSize         = (uint)Marshal.SizeOf(typeof(VSCOMPONENTSELECTORTABINIT));
			tabInit[1].varTabInitInfo = 0;
			tabInit[1].guidTab        = VSConstants.GUID_COMClassicPage;

			// Add the Project page.
			//
			tabInit[2].dwSize         = (uint)Marshal.SizeOf(typeof(VSCOMPONENTSELECTORTABINIT));
			// Tell the Add Reference dialog to call hierarchies GetProperty with 
			// the following propID to enable filtering out ourself from the Project
			// to Project reference
			tabInit[2].varTabInitInfo = (int)__VSHPROPID.VSHPROPID_ShowProjInSolutionPage;
			tabInit[2].guidTab        = VSConstants.GUID_SolutionPage;

			// Add the Browse page.
			//
			tabInit[3].dwSize         = (uint)Marshal.SizeOf(typeof(VSCOMPONENTSELECTORTABINIT));
			tabInit[3].varTabInitInfo = 0;
			tabInit[3].guidTab        = VSConstants.GUID_BrowseFilePage;

			// Add the Recent page.
			//
			tabInit[4].dwSize         = (uint)Marshal.SizeOf(typeof (VSCOMPONENTSELECTORTABINIT));
			tabInit[4].varTabInitInfo = 0;
			tabInit[4].guidTab        = GUID_MruPage;

			uint pX = 0, pY = 0;

			startOnTab = tabInit[2].guidTab;

			componentDialog = GetService(typeof (SVsComponentSelectorDlg)) as IVsComponentSelectorDlg2;

			try
			{
				// Call the container to open the add reference dialog.
				//
				if (componentDialog != null)
				{
					// Let the project know not to show itself in the Add Project Reference Dialog page
					//
					ShowProjectInSolutionPage = false;

					// Call the container to open the add reference dialog.
					//
					ErrorHandler.ThrowOnFailure(
						componentDialog.ComponentSelectorDlg2(
							(UInt32)
								(__VSCOMPSELFLAGS.VSCOMSEL_MultiSelectMode |
								 __VSCOMPSELFLAGS.VSCOMSEL_IgnoreMachineName),
							this,
							0,
							null,
							// Title
							Microsoft.VisualStudio.Package.SR.GetString(
								Microsoft.VisualStudio.Package.SR.AddReferenceDialogTitle),
							"VS.AddReference", // Help topic
							ref pX,
							ref pY,
							(uint)tabInit.Length,
							tabInit,
							ref startOnTab, 
							"*.dll",
							ref browseLocations));
				}
			}
			catch (COMException e)
			{
				Trace.WriteLine("Exception : " + e.Message);
				return e.ErrorCode;
			}
			finally
			{
				// Let the project know it can show itself in the Add Project Reference Dialog page
				//
				ShowProjectInSolutionPage = true;
			}

			return VSConstants.S_OK;
		}

		protected override ConfigProvider CreateConfigProvider()
		{
			return new NemerleConfigProvider(this);
		}

		protected override NodeProperties CreatePropertiesObject()
		{
			return new NemerleProjectNodeProperties(this);
		}

		public override int AddItem(uint itemIdLoc, VSADDITEMOPERATION op, string itemName, uint filesToOpen, string[] files, IntPtr dlgOwner, VSADDRESULT[] result)
		{
			return AddManyItemsHelper(itemIdLoc, op, itemName, filesToOpen, files, dlgOwner, result);
		}

		/// <summary>
		/// Allows you to query the project for special files and optionally create them. 
		/// </summary>
		/// <param name="fileId">__PSFFILEID of the file</param>
		/// <param name="flags">__PSFFLAGS flags for the file</param>
		/// <param name="itemid">The itemid of the node in the hierarchy</param>
		/// <param name="fileName">The file name of the special file.</param>
		/// <returns></returns>
		public override int GetFile(int fileId, uint flags, out uint itemid, out string fileName)
		{
			switch (fileId)
			{
				case (int)__PSFFILEID.PSFFILEID_AppConfig:
					fileName = "app.config";
					break;
				case (int)__PSFFILEID.PSFFILEID_Licenses:
					fileName = "licenses.licx";
					break;

				case (int)__PSFFILEID2.PSFFILEID_WebSettings:
					fileName = "web.config";
					break;
				case (int)__PSFFILEID2.PSFFILEID_AppManifest:
					fileName = "app.manifest";
					break;
				case (int)__PSFFILEID2.PSFFILEID_AppSettings:
					fileName = "Settings.settings";
					break;
				case (int)__PSFFILEID2.PSFFILEID_AssemblyResource:
					fileName = "Resources.resx";
					break;
				case (int)__PSFFILEID2.PSFFILEID_AssemblyInfo:
					fileName = "AssemblyInfo.cs";
					break;
				default:
					return base.GetFile(fileId, flags, out itemid, out fileName);
			}

			HierarchyNode fileNode = FindChild(fileName);
			string fullPath = Path.Combine(ProjectFolder, fileName);

			if (fileNode == null && (flags & (uint)__PSFFLAGS.PSFF_CreateIfNotExist) != 0)
			{
				// Create a zero-length file if not exist already.
				//
				if (!File.Exists(fullPath))
					File.WriteAllText(fullPath, string.Empty);

				fileNode = CreateFileNode(fileName);
				AddChild(fileNode);
			}

			itemid = fileNode != null? fileNode.ID: 0;

			if ((flags & (uint)__PSFFLAGS.PSFF_FullPath) != 0)
				fileName = fullPath;

			return VSConstants.S_OK;
		}

		protected override void OnHandleConfigurationRelatedGlobalProperties(object sender, ActiveConfigurationChangedEventArgs eventArgs)
		{
			base.OnHandleConfigurationRelatedGlobalProperties(sender, eventArgs);

			_projectInfo.UpdateConditionalVariables();
		}

		public override int GetGuidProperty(int propid, out Guid guid)
		{
			if ((__VSHPROPID)propid == __VSHPROPID.VSHPROPID_PreferredLanguageSID)
			{
				guid = typeof(NemerleLanguageService).GUID;
			}
			else
			{
				return base.GetGuidProperty(propid, out guid);
			}
			return VSConstants.S_OK;
		}

		protected override bool IsItemTypeFileType(string type)
		{
			if (base.IsItemTypeFileType(type))
				return true;

			return
				String.Compare(type, "Page",                  StringComparison.OrdinalIgnoreCase) == 0 ||
				String.Compare(type, "ApplicationDefinition", StringComparison.OrdinalIgnoreCase) == 0 ||
				String.Compare(type, "Resource",              StringComparison.OrdinalIgnoreCase) == 0;
		}

		#endregion

		#region IVsProjectSpecificEditorMap2 Members

		public int GetSpecificEditorProperty(string mkDocument, int propid, out object result)
		{
			// Initialize output params.
			//
			result = null;

			// Validate input.
			//
			if (string.IsNullOrEmpty(mkDocument))
				throw new ArgumentException("Was null or empty", "mkDocument");

			// Make sure that the document moniker passed to us is part of this project.
			// We also don't care if it is not a nemerle file node.
			//
			uint itemid;

			ErrorHandler.ThrowOnFailure(ParseCanonicalName(mkDocument, out itemid));

			HierarchyNode hierNode = NodeFromItemId(itemid);

			if (hierNode == null || ((hierNode as NemerleFileNode) == null))
				return VSConstants.E_NOTIMPL;

			switch (propid)
			{
				case (int)__VSPSEPROPID.VSPSEPROPID_UseGlobalEditorByDefault:
					// We do not want to use global editor for form files.
					//
					result = true;
					break;

				case (int)__VSPSEPROPID.VSPSEPROPID_ProjectDefaultEditorName:
					result = "Nemerle Form Editor";
					break;
			}

			return VSConstants.S_OK;
		}

		public int GetSpecificEditorType(string mkDocument, out Guid guidEditorType)
		{
			// Ideally we should at this point initalize a File extension to 
			// EditorFactory guid Map e.g. in the registry hive so that more 
			// editors can be added without changing this part of the code. 
			// Nemerle only makes usage of one Editor Factory and therefore 
			// we will return that guid.
			//
			guidEditorType = NemerleEditorFactory.EditorFactoryGuid;
			return VSConstants.S_OK;
		}

		int IVsProjectSpecificEditorMap2.GetSpecificLanguageService(string mkDocument, out Guid guidLanguageService)
		{
			guidLanguageService = Guid.Empty;
			return VSConstants.E_NOTIMPL;

			// AKhropov: looks like it's more appropriate
			// IT: No, it is not. It makes the integration not working.
			//guidLanguageService = Utils.GetService<NemerleLanguageService>(Site).GetLanguageServiceGuid();
			//return VSConstants.S_OK;
		}

		public int SetSpecificEditorProperty(string mkDocument, int propid, object value)
		{
			return VSConstants.E_NOTIMPL;
		}

		#endregion

		#region Static Methods

		public static string GetOutputExtension(OutputType outputType)
		{
			switch (outputType)
			{
				case OutputType.Library: return ".dll";
				case OutputType.Exe:
				case OutputType.WinExe:  return ".exe";
			}

			throw new InvalidOperationException();
		}

		#endregion

		#region Helper methods

		private int AddManyItemsHelper(uint itemIdLoc, VSADDITEMOPERATION op, string itemName, uint filesToOpen, string[] files, IntPtr dlgOwner, VSADDRESULT[] result)
		{
			List<string> actualFiles = new List<string>(files.Length);
			List<string> dirs = new List<string>();
			foreach (string file in files)
			{
				if (File.Exists(file))
					actualFiles.Add(file);
				else
				{
					if (Directory.Exists(file))
					{
						dirs.Add(file);
					}
				}
			}

			if (actualFiles.Count > 0)
				ErrorHandler.ThrowOnFailure(base.AddItem(itemIdLoc, op, itemName, (uint) actualFiles.Count, actualFiles.ToArray(), dlgOwner, result));

			foreach (string directory in dirs)
			{
				HierarchyNode folderNode = CreateFolderNodeHelper(directory, itemIdLoc);
				List<string> directoryEntries = new List<string>();
				directoryEntries.AddRange(Directory.GetFiles(directory));
				directoryEntries.AddRange(Directory.GetDirectories(directory));
				AddManyItemsHelper(folderNode.ID, op, null, (uint) directoryEntries.Count, directoryEntries.ToArray(), dlgOwner, result);
			}

			return VSConstants.S_OK;
		}

		private HierarchyNode CreateFolderNodeHelper(string directory, uint parentID)
		{
			if (!Path.GetFullPath(directory).Contains(this.ProjectFolder))
			{
				HierarchyNode parent = this.NodeFromItemId(parentID);
				if (parent is FolderNode)
					return CreateFolderNodeHelper(directory, parent.Url);

				if (parent is ProjectNode)
					return CreateFolderNodeHelper(directory, Path.GetDirectoryName(parent.Url));
			}

			return base.CreateFolderNodes(directory);
		}

		private HierarchyNode CreateFolderNodeHelper(string sourcePath, string parentPath)
		{
			string newFolderUrl = Path.Combine(parentPath, Path.GetFileName(sourcePath));
			Directory.CreateDirectory(newFolderUrl);
			return base.CreateFolderNodes(newFolderUrl);
		}

		#endregion
	}
}