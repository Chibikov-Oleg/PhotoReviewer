﻿using System;
using System.Linq.Expressions;
using System.Windows;
// ReSharper disable UnusedMember.Global
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable IntroduceOptionalParameters.Global

namespace PhotoReviewer
{
    public static class DependencyProperty<T> where T : DependencyObject
    {
        public static DependencyProperty Register<TProperty>(Expression<Func<T, TProperty>> propertyExpression)
        {
            return Register(propertyExpression, default(TProperty), null);
        }

        public static DependencyProperty Register<TProperty>(Expression<Func<T, TProperty>> propertyExpression,
            TProperty defaultValue)
        {
            return Register(propertyExpression, defaultValue, null);
        }

        public static DependencyProperty Register<TProperty>(Expression<Func<T, TProperty>> propertyExpression,
            Func<T, PropertyChangedCallback<TProperty>> propertyChangedCallbackFunc)
        {
            return Register(propertyExpression, default(TProperty), propertyChangedCallbackFunc);
        }

        public static DependencyProperty Register<TProperty>(Expression<Func<T, TProperty>> propertyExpression,
            TProperty defaultValue, Func<T, PropertyChangedCallback<TProperty>> propertyChangedCallbackFunc)
        {
            var propertyName = propertyExpression.RetrieveMemberName();
            var callback = ConvertCallback(propertyChangedCallbackFunc);

            return DependencyProperty.Register(propertyName, typeof(TProperty), typeof(T),
                new PropertyMetadata(defaultValue, callback));
        }

        private static PropertyChangedCallback ConvertCallback<TProperty>(Func<T, PropertyChangedCallback<TProperty>> propertyChangedCallbackFunc)
        {
            if (propertyChangedCallbackFunc == null)
                return null;
            return (d, e) =>
            {
                var callback = propertyChangedCallbackFunc((T) d);
                callback?.Invoke(new DependencyPropertyChangedEventArgs<TProperty>(e));
            };
        }
    }

    public delegate void PropertyChangedCallback<TProperty>(DependencyPropertyChangedEventArgs<TProperty> e);

    public class DependencyPropertyChangedEventArgs<T> : EventArgs
    {
        public DependencyPropertyChangedEventArgs(DependencyPropertyChangedEventArgs e)
        {
            NewValue = (T) e.NewValue;
            OldValue = (T) e.OldValue;
            Property = e.Property;
        }

        public T NewValue { get; private set; }
        public T OldValue { get; private set; }
        public DependencyProperty Property { get; private set; }
    }

    public static class ExpressionExtensions
    {
        public static string RetrieveMemberName<TArg, TRes>(this Expression<Func<TArg, TRes>> propertyExpression)
        {
            var memberExpression = propertyExpression.Body as MemberExpression;
            if (memberExpression == null)
            {
                var unaryExpression = propertyExpression.Body as UnaryExpression;
                if (unaryExpression != null)
                    memberExpression = unaryExpression.Operand as MemberExpression;
            }
            var parameterExpression = memberExpression?.Expression as ParameterExpression;
            if (parameterExpression != null && parameterExpression.Name == propertyExpression.Parameters[0].Name)
                return memberExpression.Member.Name;
            throw new ArgumentException("Invalid expression.", nameof(propertyExpression));
        }
    }
}