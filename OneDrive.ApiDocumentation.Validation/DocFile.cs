﻿namespace OneDrive.ApiDocumentation.Validation
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using System.IO;

    /// <summary>
    /// A documentation file that may contain one more resources or API methods
    /// </summary>
    public class DocFile
    {
        #region Instance Variables
        protected bool m_hasScanRun;
        protected string m_BasePath;
        protected List<MarkdownDeep.Block> m_CodeBlocks = new List<MarkdownDeep.Block>();
        protected List<ResourceDefinition> m_Resources = new List<ResourceDefinition>();
        protected List<MethodDefinition> m_Requests = new List<MethodDefinition>();
        protected List<ResourceDefinition> m_JsonExamples = new List<ResourceDefinition>();

//        protected List<MarkdownDeep.LinkInfo> m_Links = new List<MarkdownDeep.LinkInfo>();
        #endregion

        #region Properties
        /// <summary>
        /// Friendly name of the file
        /// </summary>
        public string DisplayName { get; protected set; }

        /// <summary>
        /// Path to the file on disk
        /// </summary>
        public string FullPath { get; protected set; }

        /// <summary>
        /// HTML-rendered version of the markdown source (for displaying)
        /// </summary>
        public string HtmlContent { get; protected set; }

        public ResourceDefinition[] Resources
        {
            get { return m_Resources.ToArray(); }
        }

        public MethodDefinition[] Requests
        {
            get { return m_Requests.ToArray(); }
        }

        public string[] LinkDestinations
        {
            get
            {
                var query = from p in MarkdownLinks
                            select p.def.url;
                return query.ToArray();
            }
        }

        /// <summary>
        /// Raw Markdown parsed blocks
        /// </summary>
        protected MarkdownDeep.Block[] OriginalMarkdownBlocks { get; set; }

        protected List<MarkdownDeep.LinkInfo> MarkdownLinks {get;set;}
        #endregion

        #region Constructor

        protected DocFile()
        {

        }

        public DocFile(string basePath, string relativePath)
        {
            m_BasePath = basePath;
            FullPath = Path.Combine(basePath, relativePath.Substring(1));
            DisplayName = relativePath;
        }
        #endregion

        #region Markdown Parsing

        protected void TransformMarkdownIntoBlocksAndLinks(string inputMarkdown)
        {
            MarkdownDeep.Markdown md = new MarkdownDeep.Markdown();
            md.SafeMode = false;
            md.ExtraMode = true;

            HtmlContent = md.Transform(inputMarkdown);
            OriginalMarkdownBlocks = md.Blocks;
            MarkdownLinks = new List<MarkdownDeep.LinkInfo>(md.FoundLinks);
        }


        /// <summary>
        /// Read the contents of the file into blocks and generate any resource or method definitions from the contents
        /// </summary>
        public bool Scan(out ValidationError[] errors)
        {
            m_hasScanRun = true;
            List<ValidationError> detectedErrors = new List<ValidationError>();
            
            try
            {
                using (StreamReader reader = File.OpenText(this.FullPath))
                {
                    TransformMarkdownIntoBlocksAndLinks(reader.ReadToEnd());
                }
            }
            catch (IOException ioex)
            {
                detectedErrors.Add(new ValidationError(ValidationErrorCode.ErrorOpeningFile, DisplayName, "Error reading file contents: {0}", ioex.Message));
                errors = detectedErrors.ToArray();
                return false;
            }
            catch (Exception ex)
            {
                detectedErrors.Add(new ValidationError(ValidationErrorCode.ErrorReadingFile, DisplayName, "Error reading file contents: {0}", ex.Message));
                errors = detectedErrors.ToArray();
                return false;
            }

            return ParseMarkdownBlocks(out errors);
        }

        private static TextWriter datawriter;
        private static TextWriter GetWriter()
        {
            if (null == datawriter)
            {
                datawriter = new StreamWriter("doc-schema.txt") { AutoFlush = true };
            }
            return datawriter;
        }

        protected bool ParseMarkdownBlocks(out ValidationError[] errors)
        {
            List<ValidationError> detectedErrors = new List<ValidationError>();

            var writer = GetWriter();
            writer.WriteLine();
            writer.WriteLine("### " + this.DisplayName + " ###");

            string pageTitle = null;
            string pageDescription = null;

            MarkdownDeep.Block previousHeaderBlock = null;

            List<object> StuffFoundInThisDoc = new List<object>();

            for (int i = 0; i < OriginalMarkdownBlocks.Length; i++)
            {
                var block = OriginalMarkdownBlocks[i];

                // Capture the first h1 and/or p element to be used as the title and description for items on this page
                if (block.BlockType == MarkdownDeep.BlockType.h1 && pageTitle == null)
                {
                    pageTitle = block.Content;
                    detectedErrors.Add(new ValidationMessage(null, "Found page title: {0}", pageTitle));
                }
                else if (block.BlockType == MarkdownDeep.BlockType.p && pageDescription == null)
                {
                    pageDescription = block.Content;
                    detectedErrors.Add(new ValidationMessage(null, "Found page description: {0}", pageDescription));
                }
                else if (block.BlockType == MarkdownDeep.BlockType.html)
                {
                    // If the next block is a codeblock we've found a metadata + codeblock pair
                    MarkdownDeep.Block nextBlock = null;
                    if (i + 1 < OriginalMarkdownBlocks.Length)
                    {
                        nextBlock = OriginalMarkdownBlocks[i + 1];
                    }
                    if (null != nextBlock && nextBlock.BlockType == MarkdownDeep.BlockType.codeblock)
                    {
                        // html + codeblock = likely request or response!
                        var definition = ParseCodeBlock(block, nextBlock);
                        if (null != definition)
                        {
                            detectedErrors.Add(new ValidationMessage(null, "Found code block: {0} [{1}]", definition.Title, definition.GetType().Name));
                            definition.Title = pageTitle;
                            definition.Description = pageDescription;

                            if (!StuffFoundInThisDoc.Contains(definition))
                            {
                                StuffFoundInThisDoc.Add(definition);
                            }
                        }
                    }
                }
                else if (block.BlockType == MarkdownDeep.BlockType.table_spec)
                {
                    MarkdownDeep.Block blockBeforeTable = (i - 1 >= 0) ? OriginalMarkdownBlocks[i - 1] : null;
                    if (null == blockBeforeTable) continue;

                    ItemDefinition[] rows;
                    ValidationError[] parseErrors;
                    var table = TableSpecConverter.ParseTableSpec(block, previousHeaderBlock, out parseErrors);
                    if (null != parseErrors) detectedErrors.AddRange(parseErrors);

                    detectedErrors.Add(new ValidationMessage(null, "Found table: {0}. Rows:\r\n{1}", table.Type,
                        (from r in table.Rows select Newtonsoft.Json.JsonConvert.SerializeObject(r, Newtonsoft.Json.Formatting.Indented)).ComponentsJoinedByString(" ,\r\n")));

                    // TODO: Attach the table to something meaningful in this DocFile. Ideally to a MethodDefinition or ResourceDefinition
                    StuffFoundInThisDoc.Add(table);
                }

                if (block.IsHeaderBlock())
                {
                    previousHeaderBlock = block;
                }
            }

            ValidationError[] postProcessingErrors;
            PostProcessFoundElements(StuffFoundInThisDoc, out postProcessingErrors);
            detectedErrors.AddRange(postProcessingErrors);
            
            errors = detectedErrors.ToArray();
            return !detectedErrors.Any(x => x.IsError);
        }

        private void PostProcessFoundElements(List<object> StuffFoundInThisDoc, out ValidationError[] postProcessingErrors)
        {
            /*
            if FoundMethods == 1 then
              Attach all tables found in the document to the method.

            else if FoundMethods > 1 then
              Table.Type == ErrorCodes
                - Attach errors to all methods in the file
              Table.Type == PathParameters
                - Find request with matching parameters
              Table.Type == Query String Parameters
                - Request may not have matching parameters, because query string parameters may not be part of the request
              Table.Type == Header Parameters
                - Find request with matching parameters
              Table.Type == Body Parameters
                - Find request with matching parameters
             */

            var foundMethods = from s in StuffFoundInThisDoc
                               where s is MethodDefinition
                               select (MethodDefinition)s;

            var foundTables = from s in StuffFoundInThisDoc
                                   where s is TableDefinition
                                   select (TableDefinition)s;

            if (foundMethods.Count() == 1)
            {
                var onlyMethod = foundMethods.Single();
                foreach (var table in foundTables)
                {
                    switch (table.Type)
                    {
                        case TableBlockType.EnumerationValues:
                            // TODO: Support enumeration values
                            break;
                        case TableBlockType.ErrorCodes:
                            onlyMethod.Errors = table.Rows.Cast<ErrorDefinition>().ToArray();
                            break;

                        case TableBlockType.HttpHeaders:
                        case TableBlockType.PathParameters:
                        case TableBlockType.QueryStringParameters:
                            List<ParameterDefinition> parameters = new List<ParameterDefinition>(onlyMethod.Parameters);
                            parameters.AddRange(table.Rows.Cast<ParameterDefinition>());
                            onlyMethod.Parameters = parameters.ToArray();
                            break;

                        case TableBlockType.RequestObjectProperties:
                        case TableBlockType.ResourcePropertyDescriptions:
                        case TableBlockType.ResponseObjectProperties:
                            break;
                    }
                }
            }

            postProcessingErrors = new ValidationError[0];
        }

        /// <summary>
        /// Parse through the markdown blocks and intprerate the documents into
        /// our internal object model.
        /// </summary>
        /// <returns><c>true</c>, if code blocks was parsed, <c>false</c> otherwise.</returns>
        /// <param name="errors">Errors.</param>
        protected bool ParseMarkdownBlocksOld(out ValidationError[] errors)
        {
            List<ValidationError> detectedErrors = new List<ValidationError>();

            // Scan through the blocks to find something interesting
            m_CodeBlocks = FindCodeBlocks(OriginalMarkdownBlocks);

            for (int i = 0; i < m_CodeBlocks.Count; )
            {
                // We're looking for pairs of html + code blocks. The HTML block contains metadata about the block.
                // If we don't find an HTML block, then we skip the code block.
                var htmlComment = m_CodeBlocks[i];
                if (htmlComment.BlockType != MarkdownDeep.BlockType.html)
                {
                    detectedErrors.Add(new ValidationMessage(FullPath, "Block skipped - expected HTML comment, found: {0}", htmlComment.BlockType, htmlComment.Content));
                    i++;
                    continue;
                }

                try
                {
                    var codeBlock = m_CodeBlocks[i + 1];
                    ParseCodeBlock(htmlComment, codeBlock);
                }
                catch (Exception ex)
                {
                    detectedErrors.Add(new ValidationError(ValidationErrorCode.MarkdownParserError, FullPath, "Exception while parsing code blocks: {0}.", ex.Message));
                }
                i += 2;
            }

            errors = detectedErrors.ToArray();
            return detectedErrors.Count == 0;
        }

        /// <summary>
        /// Filters the blocks to just a collection of blocks that may be
        /// relevent for our purposes
        /// </summary>
        /// <returns>The code blocks.</returns>
        /// <param name="blocks">Blocks.</param>
        protected static List<MarkdownDeep.Block> FindCodeBlocks(MarkdownDeep.Block[] blocks)
        {
            var blockList = new List<MarkdownDeep.Block>();
            foreach (var block in blocks)
            {
                switch (block.BlockType)
                {
                    case MarkdownDeep.BlockType.codeblock:
                    case MarkdownDeep.BlockType.html:
                        blockList.Add(block);
                        break;
                    default:
                        break;
                }
            }
            return blockList;
        }

        /// <summary>
        /// Convert an annotation and fenced code block in the documentation into something usable. Adds
        /// the detected object into one of the internal collections of resources, methods, or examples.
        /// </summary>
        /// <param name="metadata"></param>
        /// <param name="code"></param>
        public ItemDefinition ParseCodeBlock(MarkdownDeep.Block metadata, MarkdownDeep.Block code)
        {
            if (metadata.BlockType != MarkdownDeep.BlockType.html)
                throw new ArgumentException("metadata block does not appear to be metadata");

            if (code.BlockType != MarkdownDeep.BlockType.codeblock)
                throw new ArgumentException("code block does not appear to be code");

            var metadataJsonString = metadata.Content.Substring(4, metadata.Content.Length - 9);
            var annotation = CodeBlockAnnotation.FromJson(metadataJsonString);

            switch (annotation.BlockType)
            {
                case CodeBlockType.Resource:
                    {
                        var resource = new ResourceDefinition(annotation, code.Content, this);
                        m_Resources.Add(resource);
                        return resource;
                    }
                case CodeBlockType.Request:
                    {
                        var method = MethodDefinition.FromRequest(code.Content, annotation, this);
                        if (string.IsNullOrEmpty(method.Identifier))
                            method.Identifier = string.Format("{0} #{1}", DisplayName, m_Requests.Count);
                        m_Requests.Add(method);
                        return method;
                    }

                case CodeBlockType.Response:
                    {
                        var method = m_Requests.Last();
                        method.AddExpectedResponse(code.Content, annotation);
                        return method;
                    }
                case CodeBlockType.Example:
                    {
                        var example = new ExampleDefinition(annotation, code.Content, this);
                        m_JsonExamples.Add(example);
                        return example;
                    }
                case CodeBlockType.Ignored:
                    return null;
                default:
                    throw new NotSupportedException("Unsupported block type: " + annotation.BlockType);
            }
        }

        public MarkdownDeep.Block[] CodeBlocks
        {
            get { return m_CodeBlocks.ToArray(); }
        }
        #endregion

        #region Link Verification

        /// <summary>
        /// Checks all links detected in the source document to make sure they are valid.
        /// </summary>
        /// <param name="errors">Information about broken links</param>
        /// <returns>True if all links are valid. Otherwise false</returns>
        public bool ValidateNoBrokenLinks(bool includeWarnings, out ValidationError[] errors)
        {
            if (!m_hasScanRun)
                throw new InvalidOperationException("Cannot validate links until Scan() is called.");

            var foundErrors = new List<ValidationError>();
            foreach (var link in MarkdownLinks)
            {
                if (null == link.def)
                {
                    foundErrors.Add(new ValidationError(ValidationErrorCode.MissingLinkSourceId, this.DisplayName, "Link specifies ID '{0}' which was not found in the document.", link.link_text));
                    continue;
                }

                var result = VerifyLink(FullPath, link.def.url, m_BasePath);
                switch (result)
                {
                    case LinkValidationResult.BookmarkSkipped:
                    case LinkValidationResult.ExternalSkipped:
                        if (includeWarnings)
                            foundErrors.Add(new ValidationWarning(ValidationErrorCode.LinkValidationSkipped, this.DisplayName, "Skipped validation of link '{1}' to URL '{0}'", link.def.url, link.link_text));
                        break;
                    case LinkValidationResult.FileNotFound:
                        foundErrors.Add(new ValidationError(ValidationErrorCode.LinkDestinationNotFound, this.DisplayName, "Destination missing for link '{1}' to URL '{0}'", link.def.url, link.link_text));
                        break;
                    case LinkValidationResult.ParentAboveDocSetPath:
                        foundErrors.Add(new ValidationError(ValidationErrorCode.LinkDestinationOutsideDocSet, this.DisplayName, "Destination outside of doc set for link '{1}' to URL '{0}'", link.def.url, link.link_text));
                        break;
                    case LinkValidationResult.UrlFormatInvalid:
                        foundErrors.Add(new ValidationError(ValidationErrorCode.LinkFormatInvalid, this.DisplayName, "Invalid URL format for link '{1}' to URL '{0}'", link.def.url, link.link_text));
                        break;
                    case LinkValidationResult.Valid:
                        foundErrors.Add(new ValidationMessage(this.DisplayName, "Link to URL '{0}' is valid.", link.def.url, link.link_text));
                        break;
                    default:
                        foundErrors.Add(new ValidationError(ValidationErrorCode.Unknown, this.DisplayName, "{2}: for link '{1}' to URL '{0}'", link.def.url, link.link_text, result));
                        break;

                }
            }

            errors = foundErrors.ToArray();
            return errors.Length == 0;
        }

        protected enum LinkValidationResult
        {
            Valid,
            FileNotFound,
            UrlFormatInvalid,
            ExternalSkipped,
            BookmarkSkipped,
            ParentAboveDocSetPath
        }

        protected LinkValidationResult VerifyLink(string docFilePath, string linkUrl, string docSetBasePath)
        {
            Uri parsedUri;
            var validUrl = Uri.TryCreate(linkUrl, UriKind.RelativeOrAbsolute, out parsedUri);

            FileInfo sourceFile = new FileInfo(docFilePath);

            if (validUrl)
            {
                if (parsedUri.IsAbsoluteUri && (parsedUri.Scheme == "http" || parsedUri.Scheme == "https"))
                {
                    // TODO: verify the URL is valid
                    return LinkValidationResult.ExternalSkipped;
                }
                else if (linkUrl.StartsWith("#"))
                {
                    // TODO: bookmark link within the same document
                    return LinkValidationResult.BookmarkSkipped;
                }
                else
                {
                    return VerifyRelativeLink(sourceFile, linkUrl, docSetBasePath);
                }
            }
            else
            {
                return LinkValidationResult.UrlFormatInvalid;
            }
        }

        protected virtual LinkValidationResult VerifyRelativeLink(FileInfo sourceFile, string linkUrl, string docSetBasePath)
        {
            var rootPath = sourceFile.DirectoryName;
            if (linkUrl.Contains("#"))
            {
                linkUrl = linkUrl.Substring(0, linkUrl.IndexOf("#"));
            }
            while (linkUrl.StartsWith(".." + Path.DirectorySeparatorChar))
            {
                var nextLevelParent = new DirectoryInfo(rootPath).Parent;
                rootPath = nextLevelParent.FullName;
                linkUrl = linkUrl.Substring(3);
            }

            if (rootPath.Length < docSetBasePath.Length)
            {
                return LinkValidationResult.ParentAboveDocSetPath;
            }

            var pathToFile = Path.Combine(rootPath, linkUrl);
            if (!File.Exists(pathToFile))
            {
                return LinkValidationResult.FileNotFound;
            }

            return LinkValidationResult.Valid;
        }

        #endregion

    }

    public enum DocType
    {
        Unknown = 0,
        Resource,
        MethodRequest
    }


}
