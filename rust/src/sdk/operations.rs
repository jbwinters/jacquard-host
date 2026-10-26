//! Rust closures serving an effect's operations.

use std::collections::BTreeMap;
use std::marker::PhantomData;
use std::sync::Arc;

use crate::host::{HandlerContext, HandlerError};
use crate::protocol::Value;

use super::data::{Data, Decode, Encode};
use super::identities::normal;

/// A typed handler with its argument mapping erased.
pub(crate) type Erased = Arc<dyn Fn(&Data, &HandlerContext, &[Value]) -> Result<Value, HandlerError> + Send + Sync>;

/// One effect's service: operation names mapped to Rust closures.
///
/// Names are store spellings such as `"lookup-stock"`, or Rust spellings
/// such as `"lookup_stock"`. A closure's parameters decode the request's
/// arguments; a leading `&HandlerContext` parameter reports cancellation.
/// It returns `Result<T, HandlerError>`, where `T` encodes the result (`()`
/// for unit). A structured [`HandlerError`] is reported as that failure; any
/// other error raised with `?`, or a panic, is reported as completion
/// unknown.
///
/// ```
/// use jacquard_host::host::{HandlerContext, HandlerError};
/// use jacquard_host::sdk::Operations;
///
/// let inventory = Operations::new()
///     .on("lookup-stock", |sku: String| Ok(if sku == "sensor" { 20 } else { 0 }))
///     .on("reserve", |context: &HandlerContext, sku: String, units: i64| {
///         if context.is_cancelled() {
///             return Err(HandlerError::refused("cancelled"));
///         }
///         Ok(())
///     });
/// ```
#[derive(Clone, Default)]
pub struct Operations {
    pub(crate) handlers: BTreeMap<String, Erased>,
}

impl Operations {
    /// An empty service.
    pub fn new() -> Self {
        Self::default()
    }

    /// Serves `operation` with `handler`, replacing any earlier handler for
    /// the same operation.
    pub fn on<M, F: IntoHandler<M>>(mut self, operation: &str, handler: F) -> Self {
        self.handlers.insert(normal(operation), handler.into_handler());
        self
    }
}

impl std::fmt::Debug for Operations {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_set().entries(self.handlers.keys()).finish()
    }
}

/// A closure that can serve an operation; see [`Operations`]. `M` describes
/// its parameters.
pub trait IntoHandler<M>: Send + Sync + 'static {
    #[doc(hidden)]
    fn into_handler(self) -> Erased;
}

/// The parameter description of a handler whose first parameter is
/// `&HandlerContext`.
pub struct WithContext<A>(PhantomData<A>);

fn arguments_failed() -> HandlerError {
    HandlerError::outside_failed("handler argument cannot be mapped")
}

fn result(data: &Data, outcome: Result<impl Encode, HandlerError>) -> Result<Value, HandlerError> {
    data.encode(&outcome?).map_err(|_| HandlerError::outside_failed("handler result cannot be represented"))
}

macro_rules! handlers {
    ($($name:ident),*) => {
        impl<F, T, $($name),*> IntoHandler<($($name,)*)> for F
        where
            F: Fn($($name),*) -> Result<T, HandlerError> + Send + Sync + 'static,
            T: Encode,
            $($name: Decode,)*
        {
            #[allow(non_snake_case, unused_variables, unused_mut)]
            fn into_handler(self) -> Erased {
                Arc::new(move |data, _, arguments| {
                    const COUNT: usize = <[&str]>::len(&[$(stringify!($name)),*]);
                    if arguments.len() != COUNT {
                        return Err(HandlerError::outside_failed("handler parameter count differs from the request"));
                    }
                    let mut arguments = arguments.iter();
                    $(let $name: $name = data.decode(arguments.next().expect("counted")).map_err(|_| arguments_failed())?;)*
                    result(data, self($($name),*))
                })
            }
        }

        impl<F, T, $($name),*> IntoHandler<WithContext<($($name,)*)>> for F
        where
            F: Fn(&HandlerContext, $($name),*) -> Result<T, HandlerError> + Send + Sync + 'static,
            T: Encode,
            $($name: Decode,)*
        {
            #[allow(non_snake_case, unused_variables, unused_mut)]
            fn into_handler(self) -> Erased {
                Arc::new(move |data, context, arguments| {
                    const COUNT: usize = <[&str]>::len(&[$(stringify!($name)),*]);
                    if arguments.len() != COUNT {
                        return Err(HandlerError::outside_failed("handler parameter count differs from the request"));
                    }
                    let mut arguments = arguments.iter();
                    $(let $name: $name = data.decode(arguments.next().expect("counted")).map_err(|_| arguments_failed())?;)*
                    result(data, self(context, $($name),*))
                })
            }
        }
    };
}

handlers!();
handlers!(A);
handlers!(A, B);
handlers!(A, B, C);
handlers!(A, B, C, D);
handlers!(A, B, C, D, E);
handlers!(A, B, C, D, E, G);
