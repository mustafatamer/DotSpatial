using DotSpatial.Analysis;
using DotSpatial.Data;

namespace CodeSnippets
{
    public class BufferExamples
    {
        /// <summary>
        /// Bu kod, mevcut bir şekil dosyasının yeni bir özellik kümesi olarak nasıl açılacağını, özellikleri arabelleğe almayı ve ardından farklı bir dosyaya kaydetmeyi gösterir.
        /// </summary>
        /// <param name="fileName">Path of your shapefile (e.g. C:\myShapefile.shp).</param>
        public static void BufferFeatures(string fileName)
        {
            // Açılacak şekil dosyasının dosya yolunu iletin
            IFeatureSet fs = FeatureSet.Open(@"C:\[Your File Path]\Municipalities.shp");
         
            // "fs" özellik kümesinin özelliklerini arabelleğe alın
            IFeatureSet bs = fs.Buffer(10, true);
            
            //Arabelleğe alınan özellik kümesini yeni bir dosya olarak kaydeder
            bs.SaveAs(@"C:\[Your File Path]\Municipalities_Buffer.shp", true);
        }


        /// <summary>
        /// This code demonstrates how to use DotSpatial.Analysis.Buffer.AddBuffer.
        /// </summary>
        /// <param name="fileName">Path of your shapefile (e.g. C:\myShapefile.shp).</param>
        public static void AnalysisBufferAddBuffer(string fileName)
        {
            // Pass in the file path of the shapefile that will be opened
            IFeatureSet fs = FeatureSet.Open(fileName);

            // create an output feature set of the same feature type
            IFeatureSet fs2 = new FeatureSet(fs.FeatureType);

            // buffer the features of the first feature set by 10 and add them to the output feature set
            Buffer.AddBuffer(fs, 10, fs2);
        }

    }
}
