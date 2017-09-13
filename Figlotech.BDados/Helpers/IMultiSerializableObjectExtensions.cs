﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace Figlotech.BDados.Helpers {
    public class FTHSerializableOptions {
        public bool UseGzip { get; set; }
        public bool Formatted { get; set; }
    }

    public static class IMultiSerializableObjectExtensions {
        public static void ToJson(this IMultiSerializableObject obj, Stream rawStream, FTHSerializableOptions options = null) {
            if (obj == null)
                return;
            var StreamOptions = new BatchStreamProcessor();
            StreamOptions.Add(new GzipCompressStreamProcessor(options?.UseGzip ?? false));
            
            StreamOptions
                .Process(rawStream, (usableStream) => {

                    var json = JsonConvert.SerializeObject(obj, options.Formatted? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None);
                    using (var writter = new StreamWriter(usableStream, Encoding.UTF8)) {
                        writter.Write(json);
                    }
                });
        }

        public static void ToJsonFile(this IMultiSerializableObject obj, String fileName, FTHSerializableOptions options = null) {
            using (var fs = File.Open(fileName, FileMode.OpenOrCreate)) {
                obj.ToJson(fs, options);
            }
        }

        public static void FromJson(this IMultiSerializableObject obj, Stream rawStream, FTHSerializableOptions options = null) {
            if (obj == null)
                return;

            var StreamOptions = new BatchStreamProcessor();
            StreamOptions.Add(new GzipDecompressStreamProcessor(options?.UseGzip ?? false));

            StreamOptions
                .Process(rawStream, (usableStream) => {

                    using (var reader = new StreamReader(usableStream, Encoding.UTF8)) {
                        var json = reader.ReadToEnd();
                        try {
                            var parse = JsonConvert.DeserializeObject(json, obj.GetType());
                            FTH.MemberwiseCopy(parse, obj);
                        } catch (Exception x) {
                            FTH.WriteLine("Error parsing JSON File: " + x.Message);
                            throw x;
                        }
                    }
                });
        }

        public static void FromJsonFile(this IMultiSerializableObject obj, String fileName, FTHSerializableOptions options = null) {
            obj.FromJson(File.Open(fileName, FileMode.Open), options);
        }


        public static void ToXml(this IMultiSerializableObject obj, Stream rawStream, FTHSerializableOptions options = null) {
            if (obj == null)
                return;

            var StreamOptions = new BatchStreamProcessor();
            StreamOptions.Add(new GzipCompressStreamProcessor(options?.UseGzip ?? false));

            StreamOptions
                .Process(rawStream, (usableStream) => {

                    XmlSerializer xsSubmit = new XmlSerializer(obj.GetType());
                    var xml = "";

                    using (var sww = new StringWriter()) {

                        using (XmlTextWriter writer = new XmlTextWriter(sww)) {
                            if(options.Formatted) {
                                writer.Formatting = System.Xml.Formatting.Indented;
                                writer.Indentation = 4;
                            }
                            xsSubmit.Serialize(writer, obj);
                            xml = sww.ToString();
                            using (var sw = new StreamWriter(usableStream, Encoding.UTF8)) {
                                sw.Write(xml);
                            }
                        }
                    }
                });
        }

        public static void ToXmlFile(this IMultiSerializableObject obj, String fileName, FTHSerializableOptions options = null) {
            using (var fs = File.Open(fileName, FileMode.OpenOrCreate)) {
                obj.ToXml(fs, options);
            }
        }

        public static void FromXml(this IMultiSerializableObject obj, Stream rawStream, FTHSerializableOptions options = null) {
            if (obj == null)
                return;

            var StreamOptions = new BatchStreamProcessor();
            StreamOptions.Add(new GzipDecompressStreamProcessor(options?.UseGzip ?? false));

            StreamOptions
                .Process(rawStream, (usableStream) => {

                    var serializer = new XmlSerializer(obj.GetType());
                    // this is necessary because XML Deserializer is a bitch

                    using (StreamReader reader = new StreamReader(usableStream, Encoding.UTF8)) {
                        var retv = serializer.Deserialize(reader);
                        FTH.MemberwiseCopy(retv, obj);
                    }
                });
        }

        public static void FromXmlFile(this IMultiSerializableObject obj, String fileName, FTHSerializableOptions options = null) {
            obj.FromXml(File.Open(fileName, FileMode.Open), options);
        }
    }
}