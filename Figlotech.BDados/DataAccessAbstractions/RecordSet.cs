﻿/**
* Figlotech::Database::Entity::BDConjunto
* Extensão para List<TSource>, BDConjunto<TSource> where T: BDTabela, new()
* Permite armazenar vários objetos de um mesmo tipo e salvar/excluir em massa.
* 
*@Author: Felype Rennan Alves dos Santos
* August/2014
* 
**/
using Figlotech.Core.BusinessModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Figlotech.Core.Interfaces;

namespace Figlotech.BDados.DataAccessAbstractions {
    
    public class RecordSet<T> : List<T>, IEnumerable<T>
        where T : IDataObject, new() {


        internal IDataAccessor dataAccessor;


        public IDataAccessor DataAccessor { get; set; }

        public Type __BDadosTipoTabela {
            get {
                return typeof(T);
            }
        }

        public static int DefaultPageSize = 200;
        public int PageSize { get; set; } = DefaultPageSize;
        public bool LinearLoad = false;

        public RecordSet() { }
        public RecordSet(IDataAccessor dataAccessor) {
            this.DataAccessor = dataAccessor;
        }

        public T FirstOrDefault() {
            return this.Count > 0 ? this[0] : default(T);
        }
        
        public String CustomListing(Func<T, String> fn) {
            StringBuilder retv = new StringBuilder();
            for (int i = 0; i < this.Count; i++) {
                retv.Append(
                    String.Format(
                        $"'{fn((this[i]))}'",
                        this[i].RID
                    )
                );
                if (i < this.Count - 1)
                    retv.Append(",");
            }
            return retv.ToString();
        }

        private MemberInfo FindMember(Expression x) {
            if (x is UnaryExpression) {
                return FindMember((x as UnaryExpression).Operand);
            }
            if (x is MemberExpression) {
                return (x as MemberExpression).Member;
            }

            return null;
        }
        public MemberInfo GetOrderingMember(Expression<Func<T, object>> fn) {
            if (fn == null) return null;
            try {
                var orderingExpression = fn.Compile();
                var OrderingMember = FindMember(fn.Body);
            } catch (Exception) { }

            return null;
        }

        public RecordSet<T> OrderBy(Expression<Func<T, object>> fn, OrderingType orderingType) {
            OrderingMember = fn;
            orderingExpression = fn.Compile();
            Ordering = orderingType;

            return this;
        }

        public RecordSet<T> SetGroupingMember(MemberInfo fn) {
            GroupingMember = fn;

            return this;
        }

        public RecordSet<T> Limit(int? limits) {
            return this;
        }
        public OrderingType Ordering = OrderingType.Asc;
        public Expression<Func<T, object>> OrderingMember = null;
        public MemberInfo GroupingMember = null;
        private Func<T, object> orderingExpression = null;

        public RecordSet<T> GroupResultsBy(Expression<Func<T, object>> fn) {
            GroupingMember = GetOrderingMember(fn);

            return this;
        }


        public RecordSet<T> LoadAll(LoadAllArgs<T> args = null) {
            AddRange(Fetch(args));
            return this;
        }

        public RecordSet<T> LoadAllLinear(LoadAllArgs<T> args = null) {
            AddRange(FetchLinear(args));
            return this;
        }

        public List<T> Fetch(LoadAllArgs<T> args = null) {
            Clear();
            var agl = DataAccessor.AggregateLoad<T>(args);
            if(agl == null || agl.Any(a=> a == null)) {
                throw new BDadosException("CRITICAL DATA MAPPING ERROR!");
            }
            if (orderingExpression != null) {
                if(Ordering == OrderingType.Desc) {
                    agl = agl.OrderByDescending(orderingExpression).ToList();
                } else {
                    agl = agl.OrderBy(orderingExpression).ToList();
                }
            }
            orderingExpression = null;
            OrderingMember = null;
            GroupingMember = null;

            return agl;
        }

        public List<T> FetchLinear(LoadAllArgs<T> args = null) {
            LinearLoad = true;
            var retv = Fetch(args.NoLists());
            LinearLoad = false;
            return retv;
        }

        public bool Load(Action fn = null) {
            if (dataAccessor == null) {
                return false;
            }
            LoadAll();
            return true;
        }

        public bool Save(Action fn = null) {
            return DataAccessor?.SaveList(this) ?? false;
        }

    }
}
