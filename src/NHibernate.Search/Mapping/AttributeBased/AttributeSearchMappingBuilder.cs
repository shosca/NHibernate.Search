using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

using Iesi.Collections.Generic;

using Lucene.Net.Analysis;
using NHibernate.Properties;
using NHibernate.Search.Attributes;
using NHibernate.Search.Bridge;
using NHibernate.Search.Impl;
using NHibernate.Search.Mapping.Definition;
using NHibernate.Search.Mapping.Model;

namespace NHibernate.Search.Mapping.AttributeBased
{
    using Type = System.Type;
    
    public class AttributeSearchMappingBuilder {
		private static readonly IInternalLogger logger = LoggerProvider.LoggerFor(typeof(AttributeSearchMappingBuilder));

        private int level;
        private int maxLevel = int.MaxValue;
    	private readonly ISearchMappingDefinition mappingDefinition = new AttributedMappingDefinition(); 

        #region BuildContext class
        
        public class BuildContext
        {
            public BuildContext()
            {
                this.Processed = new HashedSet<Type>();
            }

            public DocumentMapping Root { get; set; }
            public Iesi.Collections.Generic.ISet<Type> Processed { get; private set; }
        }

        #endregion

        public DocumentMapping Build(Type type)
        {
            var documentMapping = new DocumentMapping(type)
            {
                Boost = GetBoost(type),
                IndexName = mappingDefinition.IndexedDefinition(type).Index
            };

            var context = new BuildContext
            {
                Root = documentMapping,
                Processed = { type }
            };

            BuildClass(documentMapping, true, string.Empty, context);
            BuildFilterDefinitions(documentMapping);

            return documentMapping;
        }

        private void BuildFilterDefinitions(DocumentMapping classMapping)
        {
            foreach (var defAttribute in mappingDefinition.FullTextFilters(classMapping.MappedClass))
            {
                classMapping.FullTextFilterDefinitions.Add(defAttribute);
            }
        }

        private void BuildClass(
            DocumentMapping documentMapping, bool isRoot,
            string path, BuildContext context
        )
        {
            IList<System.Type> hierarchy = new List<System.Type>();
            System.Type currClass = documentMapping.MappedClass;

            do {
                hierarchy.Add(currClass);
                currClass = currClass.BaseType;
                // NB Java stops at null we stop at object otherwise we process the class twice
                // We also need a null test for things like ISet which have no base class/interface
            } while (currClass != null && currClass != typeof(object));

            for (int index = hierarchy.Count - 1; index >= 0; index--)
            {
                currClass = hierarchy[index];
                /**
                 * Override the default analyzer for the properties if the class hold one
                 * That's the reason we go down the hierarchy
                 */

                // NB Must cast here as we want to look at the type's metadata
                var localAnalyzer = GetAnalyzer(currClass);
                var analyzer = documentMapping.Analyzer ?? localAnalyzer;

                // Check for any ClassBridges
                var classBridgeAttributes = mappingDefinition.ClassBridges(currClass);
                GetClassBridgeParameters(currClass, classBridgeAttributes);

                // Now we can process the class bridges
                foreach (var classBridgeAttribute in classBridgeAttributes)
                {
                    var bridge = BuildClassBridge(classBridgeAttribute, analyzer);
                    documentMapping.ClassBridges.Add(bridge);
                }

                // NB As we are walking the hierarchy only retrieve items at this level
                var propertyInfos = currClass.GetProperties(
                    BindingFlags.DeclaredOnly | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance
                );
                foreach (var propertyInfo in propertyInfos)
                {
                    BuildProperty(documentMapping, propertyInfo, analyzer, isRoot, path, context);
                }

                var fields = currClass.GetFields(
                    BindingFlags.DeclaredOnly | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance
                );
                foreach (var fieldInfo in fields)
                {
                    BuildProperty(documentMapping, fieldInfo, analyzer, isRoot, path, context);
                }
            }
        }

        private void BuildProperty(
            DocumentMapping documentMapping, MemberInfo member, Analyzer parentAnalyzer,
            bool isRoot, string path, BuildContext context
        )
        {
            IFieldBridge bridge = null;

            var analyzer = GetAnalyzer(member) ?? parentAnalyzer;
            var boost = GetBoost(member);
            
            var getter = GetGetterFast(documentMapping.MappedClass, member);

            var documentId = mappingDefinition.DocumentId(member);
            if (documentId != null)
            {
                string documentIdName = documentId.Name ?? member.Name;
                bridge = GetFieldBridge(member);

                if (isRoot)
                {
                    if (!(bridge is ITwoWayFieldBridge))
                    {
                        throw new SearchException("Bridge for document id does not implement TwoWayFieldBridge: " + member.Name);
                    }

                    documentMapping.DocumentId = new DocumentIdMapping(
                        documentIdName, member.Name, (ITwoWayFieldBridge)bridge, getter
                    ) { Boost = boost };
                }
                else
                {
                    // Components should index their document id
                    documentMapping.Fields.Add(new FieldMapping(
                        GetAttributeName(member, documentIdName),
                        bridge, getter
                    )
                    {
                        Store = Attributes.Store.Yes,
                        Index = Attributes.Index.UnTokenized,
                        Boost = boost
                    });
                }
            }
            
            var fieldAttributes = AttributeUtil.GetFields(member);
            if (fieldAttributes.Length > 0)
            {
                if (bridge == null)
                    bridge = GetFieldBridge(member);

                foreach (var fieldAttribute in fieldAttributes)
                {
                    var fieldAnalyzer = GetAnalyzerByType(fieldAttribute.Analyzer) ?? analyzer;
                    var field = new FieldMapping(
                        GetAttributeName(member, fieldAttribute.Name),
                        bridge, getter
                    ) {
                        Store = fieldAttribute.Store,
                        Index = fieldAttribute.Index,
                        Analyzer = fieldAnalyzer
                    };

                    documentMapping.Fields.Add(field);
                }
            }

            var embeddedAttribute = AttributeUtil.GetAttribute<IndexedEmbeddedAttribute>(member);
            if (embeddedAttribute != null)
            {
                int oldMaxLevel = maxLevel;
                int potentialLevel = embeddedAttribute.Depth + level;
                if (potentialLevel < 0)
                {
                    potentialLevel = int.MaxValue;
                }

                maxLevel = potentialLevel > maxLevel ? maxLevel : potentialLevel;
                level++;

                System.Type elementType = embeddedAttribute.TargetElement ?? GetMemberTypeOrGenericArguments(member);

                var localPrefix = embeddedAttribute.Prefix == "." ? member.Name + "." : embeddedAttribute.Prefix;

                if (maxLevel == int.MaxValue && context.Processed.Contains(elementType))
                {
                    throw new SearchException(
                        string.Format(
                            "Circular reference, Duplicate use of {0} in root entity {1}#{2}",
                            elementType.FullName,
                            context.Root.MappedClass.FullName,
                            path + localPrefix));
                }


                if (level <= maxLevel)
                {
                    context.Processed.Add(elementType); // push
                    var embedded = new EmbeddedMapping(new DocumentMapping(elementType) {
                        Boost = GetBoost(member),
                        Analyzer = GetAnalyzer(member) ?? parentAnalyzer
                    }, getter) {
                        Prefix = localPrefix
                    };

                    BuildClass(embedded.Class, false, path + localPrefix, context);

                    /**
                     * We will only index the "expected" type but that's OK, HQL cannot do downcasting either
                     */
                    // ayende: because we have to deal with generic collections here, we aren't 
                    // actually using the element type to determine what the value is, since that 
                    // was resolved to the element type of the possible collection
                    Type actualFieldType = GetMemberTypeOrGenericCollectionType(member);
                    embedded.IsCollection = typeof(IEnumerable).IsAssignableFrom(actualFieldType);

                    documentMapping.Embedded.Add(embedded);
                    context.Processed.Remove(actualFieldType); // pop
                }
                else if (logger.IsDebugEnabled)
                {
                    logger.Debug("Depth reached, ignoring " + path + localPrefix);
                }

                level--;
                maxLevel = oldMaxLevel; // set back the old max level
            }

            if (AttributeUtil.HasAttribute<ContainedInAttribute>(member))
            {
                documentMapping.ContainedIn.Add(new ContainedInMapping(getter));
            }
        }

        /// <summary>
        /// Get the attribute name out of the member unless overridden by name
        /// </summary>
        /// <param name="member"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        private string GetAttributeName(MemberInfo member, string name)
        {
            return !string.IsNullOrEmpty(name) ? name : member.Name;
        }

        // ashmind: this method is a bit too hardcoded, on the other hand it does not make
        // sense to ask IPropertyAccessor to find accessor by name when we already have MemberInfo        
        private IGetter GetGetterFast(Type type, MemberInfo member)
        {
            if (member is PropertyInfo)
                return new BasicPropertyAccessor.BasicGetter(type, (PropertyInfo)member, member.Name);
            
            if (member is FieldInfo)
                return new FieldAccessor.FieldGetter((FieldInfo)member, type, member.Name);

            throw new ArgumentException("Can not get getter for " + member.GetType() + ".", "member");
        }

        private IFieldBridge GetFieldBridge(MemberInfo member)
        {
            var memberType = GetMemberType(member);

            return BridgeFactory.GuessType(
                member.Name, memberType,
                AttributeUtil.GetFieldBridge(member),
                AttributeUtil.GetAttribute<DateBridgeAttribute>(member)
            );
        }

        private ClassBridgeMapping BuildClassBridge(IClassBridgeDefinition ann, Analyzer parentAnalyzer)
        {
            var classAnalyzer = GetAnalyzerByType(ann.Analyzer) ?? parentAnalyzer;
            return new ClassBridgeMapping(ann.Name, BridgeFactory.ExtractType(ann))
            {                           
                Boost = ann.Boost,
                Analyzer = classAnalyzer,
                Index = ann.Index,
                Store = ann.Store
            };
        }

        private Analyzer GetAnalyzer(MemberInfo member)
        {
            var attribute = AttributeUtil.GetAttribute<AnalyzerAttribute>(member);
            if (attribute == null)
                return null;

            if (!typeof(Analyzer).IsAssignableFrom(attribute.Type))
            {
                throw new SearchException("Lucene analyzer not implemented by " + attribute.Type.FullName);
            }

            return GetAnalyzerByType(attribute.Type);
        }

        private Analyzer GetAnalyzerByType(Type analyzerType)
        {
            if (analyzerType == null)
                return null;

            try {
                return (Analyzer)Activator.CreateInstance(analyzerType);
            }
            catch {
                // TODO: See if we can get a tigher exception trap here
                throw new SearchException("Failed to instantiate lucene analyzer with type  " + analyzerType.FullName);
            }
        }
        
        private static System.Type GetMemberTypeOrGenericArguments(MemberInfo member)
        {
            Type type = GetMemberType(member);
            if (type.IsGenericType)
            {
                Type[] arguments = type.GetGenericArguments();

                // if we have more than one generic arg, we assume that this is a map and return its value
                return arguments[arguments.Length - 1];
            }

            return type;
        }

        private static Type GetMemberTypeOrGenericCollectionType(MemberInfo member)
        {
            Type type = GetMemberType(member);
            return type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        }

        private static Type GetMemberType(MemberInfo member)
        {
            PropertyInfo info = member as PropertyInfo;
            return info != null ? info.PropertyType : ((FieldInfo)member).FieldType;
        }

        private float? GetBoost(ICustomAttributeProvider member)
        {
            if (member == null)
                return null;

            var boost = AttributeUtil.GetAttribute<BoostAttribute>(member);
            if (boost == null)
                return null;

            return boost.Value;
        }

    	private void GetClassBridgeParameters(Type member, IList<IClassBridgeDefinition> classBridges)
		{
			// Are we expecting any unnamed parameters?
			bool fieldBridgeExists = mappingDefinition.FieldBridge(member) != null;

			// This is a bit inefficient, but the loops will be very small
			IList<IParameterDefinition> parameters = mappingDefinition.BridgeParameters(member);

			// Do it this way around so we can ensure we process all parameters
			foreach (IParameterDefinition parameter in parameters)
			{
				// What we want to ensure is :
				// 1. If there's a field bridge, unnamed parameters belong to it
				// 2. If there's 1 class bridge and no field bridge, unnamed parameters belong to it
				// 3. If there's > 1 class bridge and no field bridge, that's an error - we don't know which class bridge should get them
				if (string.IsNullOrEmpty(parameter.Owner))
				{
					if (!fieldBridgeExists)
					{
						if (classBridges.Count == 1)
						{
							// Case 2
							classBridges[0].Parameters.Add(parameter.Name, parameter.Value);
						}
						else
						{
							// Case 3
							LogParameterError(
									"Parameter needs a name when multiple bridges defined: {0}={1}, parameter={2}",
									member,
									parameter);
						}
					}
				}
				else
				{
					bool found = false;

					// Now see if we can find the owner
					foreach (IClassBridgeDefinition classBridge in classBridges)
					{
						if (classBridge.Name == parameter.Owner)
						{
							classBridge.Parameters.Add(parameter.Name, parameter.Value);
							found = true;
							break;
						}
					}

					// Ok, did we find the appropriate class bridge?
					if (found == false)
					{
						LogParameterError(
								"No matching owner for parameter: {0}={1}, parameter={2}, owner={3}", member, parameter);
					}
				}
			}
		}

		private static void LogParameterError(string message, ICustomAttributeProvider member, IParameterDefinition parameter)
		{
			string type = string.Empty;
			string name = string.Empty;

			if (typeof(Type).IsAssignableFrom(member.GetType()))
			{
				type = "class";
				name = ((Type)member).FullName;
			}
			else if (typeof(MemberInfo).IsAssignableFrom(member.GetType()))
			{
				type = "member";
				name = ((MemberInfo)member).DeclaringType.FullName + "." + ((MemberInfo)member).DeclaringType.FullName;
			}

			// Now log it
			logger.Error(string.Format(CultureInfo.InvariantCulture, message, type, name, parameter.Name, parameter.Owner));
		}
    }
}
