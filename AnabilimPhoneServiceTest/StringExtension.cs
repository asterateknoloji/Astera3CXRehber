using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnabilimPhoneServiceTest
{
    public static class StringExtension
    {
        public static string NormalizePhoneNumber(this string number, string prefix, int minimumLength)
        {
            string result = null;
            if (number != null)
            {
                char[] array = number.Where((char c) => char.IsDigit(c)).ToArray();
                if (array.Length >= minimumLength)
                {
                    result = prefix + new string(array.Skip(array.Length - minimumLength).ToArray());
                }
            }

            return result;
        }

        public static string NormalizePhoneNumber(this string number, string prefix = "")
        {
            string result = null;
            if (number != null)
            {
                if (number.StartsWith("00"))
                {
                    number = number.Substring(2);
                }
                else if (number.StartsWith("+"))
                {
                    number = number.Substring(1);
                }

                char[] value = number.Where((char c) => char.IsDigit(c)).ToArray();
                result = prefix + new string(value);
            }

            return result;
        }

        public static string NullToString(this string Value)
        {
            if (Value != null)
            {
                return Value.ToString();
            }

            return "";
        }

        public static string ToPascalCase(this string textToChange)
        {
            CultureInfo cultureInfo = new CultureInfo("tr", useUserOverride: false);
            TextInfo textInfo = cultureInfo.TextInfo;
            StringBuilder stringBuilder = new StringBuilder();
            foreach (char value in textToChange)
            {
                stringBuilder.Append(value);
            }

            string text = stringBuilder.ToString();
            text = text.ToLower(cultureInfo);
            return textInfo.ToTitleCase(text);
        }

        public static string ToStringNumber(this int number)
        {
            if (number < 10)
            {
                return "0" + number;
            }

            return number.ToString();
        }

        public static string RemoveTurish(this string text)
        {
            return text.ToUpper().Replace("İ", "I")
                .Replace("Ğ", "G")
                .Replace("Ü", "U")
                .Replace("Ş", "S")
                .Replace("Ö", "O")
                .Replace("Ç", "C");
        }
    }
}
