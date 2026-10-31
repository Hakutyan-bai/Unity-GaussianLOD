"""Prepare a clean Unity export project and a UPM archive; never includes host scenes/model data."""
import argparse, json, os, pathlib, re, shutil, zipfile

EXPORTER = r'''
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using GaussianSplatLOD;
using GaussianSplatLOD.Editor;
public static class GaussianReleaseExport
{
    public static void Run()
    {
        string output=Environment.GetCommandLineArgs().SkipWhile(a=>a!="-releaseOutput").Skip(1).First();
        Directory.CreateDirectory(output);
        string native=GaussianLODEditorUtility.NativeExecutable;
        if(!File.Exists(native))throw new Exception("Native extraction failed.");
        string dataset=Path.Combine(Application.streamingAssetsPath,"GaussianSplatLOD/Demo");
        if(!File.Exists(Path.Combine(dataset,"lod-meta.json"))) {
            string source=Path.GetFullPath("Library/DemoSource.ply");GaussianLODDemo.WriteFixture(source);
            Directory.CreateDirectory(Path.GetDirectoryName(dataset));
            using(var job=new AholoConversionJob(native,source,dataset,5,12000,true))job.Work.GetAwaiter().GetResult();
        }
        new GaussianLODLoader(Path.Combine(dataset,"lod-meta.json")).ValidateAllRanges();
        AssetDatabase.Refresh();
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),"Assets/ReleaseBuild/ExportBase.unity");
        GaussianLODDemo.CreateScene();
        const string scene="Assets/UnityGaussianSplatLOD/Samples/Demo/Demo.unity";
        Directory.CreateDirectory(Path.GetDirectoryName(scene));AssetDatabase.Refresh();
        if(!File.Exists(scene)) {
            string error=AssetDatabase.MoveAsset(GaussianLODDemo.ScenePath,scene);
            if(!string.IsNullOrEmpty(error))throw new Exception(error);
        }
        AssetDatabase.SaveAssets();
        var shader=Resources.Load<Shader>("GaussianLOD");var sort=Resources.Load<ComputeShader>("GaussianLODSort");
        if(shader==null||!shader.isSupported||sort==null)throw new Exception("Missing or unsupported Gaussian shaders.");
        AssetDatabase.ExportPackage(new[]{"Assets/UnityGaussianSplatLOD","Assets/StreamingAssets/GaussianSplatLOD/Demo"},
            Path.Combine(output,"Unity-GaussianLOD-__RELEASE_TAG__.unitypackage"),ExportPackageOptions.Recurse);
        File.WriteAllText(Path.Combine(output,"export-validation.json"),"{\"status\":\"passed\",\"tag\":\"__RELEASE_TAG__\",\"packageVersion\":\"__PACKAGE_VERSION__\",\"unity\":\""+Application.unityVersion+"\",\"nativeExtracted\":true,\"shadersSupported\":true,\"syntheticDemoIncluded\":true}");
        Debug.Log("Gaussian __RELEASE_TAG__ export completed.");
    }
}
'''

IMPORTER = r'''
using System;
using System.IO;
using System.Linq;
using UnityEditor;
[InitializeOnLoad]
public static class GaussianReleaseImport
{
    const string Pending="GaussianReleaseImportPending";
    static GaussianReleaseImport(){EditorApplication.update+=Check;}
    public static void Run()
    {
        string package=Environment.GetCommandLineArgs().SkipWhile(a=>a!="-releasePackage").Skip(1).First();
        SessionState.SetBool(Pending,true);
        SessionState.SetString("GaussianReleaseImportStarted",DateTime.UtcNow.Ticks.ToString());
        AssetDatabase.importPackageFailed+=(name,error)=>{UnityEngine.Debug.LogError(error);EditorApplication.Exit(1);};
        AssetDatabase.ImportPackage(package,false);
    }
    static void Check()
    {
        if(!SessionState.GetBool(Pending,false))return;
        long ticks=long.Parse(SessionState.GetString("GaussianReleaseImportStarted","0"));
        if(DateTime.UtcNow.Ticks-ticks>TimeSpan.FromMinutes(5).Ticks){SessionState.SetBool(Pending,false);UnityEngine.Debug.LogError("Import timeout.");EditorApplication.Exit(1);return;}
        if(EditorApplication.isCompiling||EditorApplication.isUpdating||!File.Exists("Assets/UnityGaussianSplatLOD/Runtime/Core/GaussianLODRenderer.cs"))return;
        if(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetType("GaussianSplatLOD.GaussianLODRenderer")!=null))return;
        SessionState.SetBool(Pending,false);
        Directory.CreateDirectory("../artifacts");
        File.WriteAllText("../artifacts/import-validation.json","{\"status\":\"passed\",\"tag\":\"__RELEASE_TAG__\",\"packageVersion\":\"__PACKAGE_VERSION__\",\"resourcePackageImported\":true,\"runtimeAssemblyLoaded\":true}");
        UnityEngine.Debug.Log("Gaussian __RELEASE_TAG__ resource package imported and compiled.");EditorApplication.Exit(0);
    }
}
'''

MANIFEST = {'dependencies':{
    'com.unity.render-pipelines.universal':'14.0.11',
    'com.unity.nuget.newtonsoft-json':'3.2.1',
    'com.unity.test-framework':'1.1.33',
    'com.unity.modules.imgui':'1.0.0',
    'com.unity.modules.physics':'1.0.0',
    'com.unity.modules.jsonserialize':'1.0.0',
    'com.unity.modules.imageconversion':'1.0.0',
    'com.unity.modules.ui':'1.0.0',
    'com.unity.modules.xr':'1.0.0',
    'com.unity.modules.vr':'1.0.0',
    'com.unity.modules.unitywebrequest':'1.0.0',
    'com.unity.ugui':'1.0.0',
}}

def project(path, bootstrap, code):
    (path/'Assets/ReleaseBuild/Editor').mkdir(parents=True,exist_ok=True)
    (path/'Packages').mkdir(exist_ok=True)
    (path/'ProjectSettings').mkdir(exist_ok=True)
    (path/'Packages/manifest.json').write_text(json.dumps(MANIFEST,indent=2),encoding='utf-8')
    (path/'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 2022.3.62f3c1\n',encoding='utf-8')
    (path/'Assets/ReleaseBuild/Editor'/bootstrap).write_text(code,encoding='utf-8')

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--source',required=True);parser.add_argument('--work',required=True)
    parser.add_argument('--version',help='Package semantic version; must match source package.json (default: read source).')
    parser.add_argument('--tag',help='Release tag used in artifact filenames (default: vMAJOR.MINOR for patch zero).')
    args=parser.parse_args();source=pathlib.Path(args.source).resolve();work=pathlib.Path(args.work).resolve()
    if source==work or source in work.parents:raise ValueError('Use a work directory outside the package source.')
    metadata=json.loads((source/'package.json').read_text(encoding='utf-8-sig'))
    version=args.version or metadata['version']
    if version!=metadata['version']:raise ValueError('--version must match source package.json; source files are never rewritten.')
    if not re.fullmatch(r'\d+\.\d+\.\d+(?:-[A-Za-z0-9][A-Za-z0-9.-]*)?',version):raise ValueError('Invalid package semantic version.')
    parts=version.split('.')
    tag=args.tag or ('v'+'.'.join(parts[:2]) if parts[2]=='0' else 'v'+version)
    if not re.fullmatch(r'v\d+\.\d+(?:\.\d+)?(?:-[A-Za-z0-9][A-Za-z0-9.-]*)?',tag):raise ValueError('Invalid release tag for artifact filenames.')
    for name in ('ExportProject','ImportProject'):
        if (work/name).exists():raise ValueError('Use a new work directory; refusing to merge or clear an existing '+name+'.')
    def bootstrap(code):return code.replace('__RELEASE_TAG__',tag).replace('__PACKAGE_VERSION__',version)
    def source_files():
        excluded={'.git','Library','Temp','Logs','Distribution','__pycache__'}
        for directory,dirs,files in os.walk(source):
            dirs[:]=[d for d in dirs if d not in excluded]
            for name in sorted(files):
                path=pathlib.Path(directory)/name
                if path.suffix not in ('.unitypackage','.zip','.pyc'):yield path
    artifacts=work/'artifacts';artifacts.mkdir(parents=True,exist_ok=True)
    export=work/'ExportProject';project(export,'GaussianReleaseExport.cs',bootstrap(EXPORTER))
    project(work/'ImportProject','GaussianReleaseImport.cs',bootstrap(IMPORTER))
    target=export/'Assets/UnityGaussianSplatLOD'
    target.mkdir(parents=True,exist_ok=True)
    for path in source_files():
        rel=path.relative_to(source);parts=list(rel.parts)
        # Publication reports are separate release attachments, not Unity assets.
        if parts[0]=='Validation' or rel.as_posix()=='RELEASE_NOTES.md':continue
        # Always generate a portable synthetic scene in the isolated export project.
        if parts[0]=='Samples~' and (path.suffix=='.unity' or path.name.endswith('.unity.meta')):continue
        if 'Native~' in parts:
            if path.name!='splat-transform.exe':continue
            parts[parts.index('Native~')]='Native';parts[-1]+='.bytes'
        parts=[p[:-1] if p in ('Documentation~','Samples~') else p for p in parts]
        destination=target.joinpath(*parts);destination.parent.mkdir(parents=True,exist_ok=True)
        if path.suffix=='.md':
            destination.write_text(path.read_text(encoding='utf-8').replace('Documentation~/','Documentation/').replace('Samples~/','Samples/'),encoding='utf-8')
        elif rel.as_posix()=='package.json':
            # This is an Assets import, so tilde folders have been made visible above.
            asset_metadata=json.loads(path.read_text(encoding='utf-8-sig'))
            for sample in asset_metadata.get('samples',[]):sample['path']=sample['path'].replace('Samples~/','Samples/')
            destination.write_text(json.dumps(asset_metadata,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        else:shutil.copy2(path,destination)
    with zipfile.ZipFile(artifacts/('Unity-GaussianLOD-UPM-'+tag+'.zip'),'w',zipfile.ZIP_DEFLATED,compresslevel=6) as archive:
        for path in sorted(source_files()):
            archive.write(path,pathlib.Path('com.yourcompany.gaussian-splat-lod')/path.relative_to(source))
    print(json.dumps({'tag':tag,'packageVersion':version,'exportProject':str(export),'importProject':str(work/'ImportProject'),'artifacts':str(artifacts)},indent=2))

if __name__=='__main__':main()
