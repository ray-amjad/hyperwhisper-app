//! Test-only helpers shared by the multipart provider test modules.

use crate::contract::Part;

/// The value of the first multipart text field called `name`.
pub(crate) fn field<'a>(parts: &'a [Part], name: &str) -> Option<&'a str> {
    parts.iter().find_map(|p| match p {
        Part::Field { name: n, value } if n == name => Some(value.as_str()),
        _ => None,
    })
}

/// The values of every multipart text field called `name`, in body order.
pub(crate) fn fields<'a>(parts: &'a [Part], name: &str) -> Vec<&'a str> {
    parts
        .iter()
        .filter_map(|p| match p {
            Part::Field { name: n, value } if n == name => Some(value.as_str()),
            _ => None,
        })
        .collect()
}
